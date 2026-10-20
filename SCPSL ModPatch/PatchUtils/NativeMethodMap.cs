using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>
    /// A file-backed native function range, with an exclusive end.
    /// </summary>
    internal readonly struct NativeMethodRange
    {
        public int Offset { get; }
        public int Length { get; }
        public int FragmentCount { get; }

        internal NativeMethodRange(int offset, int length, int fragmentCount = 1)
        {
            Offset = offset;
            Length = length;
            FragmentCount = fragmentCount;
        }
    }

    /// <summary>
    /// Resolves exact AMD64 function starts using the PE exception directory.
    /// The validated range index is built once for the supplied image.
    /// </summary>
    internal sealed class NativeMethodMap
    {
        const int RUNTIME_FUNCTION_SIZE = 12;

        readonly NativeMethodRange[] _ranges;
        readonly Dictionary<int, string> _rangeErrors = new Dictionary<int, string>();
        readonly int _imageLength;

        public NativeMethodMap(byte[] image)
        {
            ArgumentNullException.ThrowIfNull(image);
            _imageLength = image.Length;

            using MemoryStream stream = new MemoryStream(image, writable: false);
            using PEReader reader = new PEReader(stream);
            PEHeaders headers = reader.PEHeaders;
            PEHeader? peHeader = headers.PEHeader;
            if (peHeader == null)
                throw new InvalidDataException("The image has no PE optional header.");
            if (headers.CoffHeader.Machine != Machine.Amd64 || peHeader.Magic != PEMagic.PE32Plus)
                throw new NotSupportedException("Native method ranges require an AMD64 PE32+ image.");
            if (peHeader.SizeOfHeaders <= 0 || peHeader.SizeOfHeaders > image.Length || peHeader.SizeOfImage <= 0)
                throw new InvalidDataException("The PE image or header size is invalid.");

            SectionRange[] sections = headers.SectionHeaders
                .Select(section => new SectionRange(section, peHeader, image.Length)).ToArray();
            ValidateSectionLayout(sections);

            DirectoryEntry directory = peHeader.ExceptionTableDirectory;
            if (directory.RelativeVirtualAddress <= 0 || directory.Size <= 0)
                throw new InvalidDataException("The PE exception directory is missing.");
            if (directory.Size % RUNTIME_FUNCTION_SIZE != 0)
                throw new InvalidDataException("The PE exception directory contains an incomplete RUNTIME_FUNCTION record.");

            NativeMethodRange table = MapRvaRange(sections,
                (uint)directory.RelativeVirtualAddress, (uint)directory.Size, executable: null,
                "PE exception directory");
            RuntimeFunction[] functions = new RuntimeFunction[directory.Size / RUNTIME_FUNCTION_SIZE];
            Dictionary<(uint Begin, uint End, uint Unwind), int> entryIndices = new Dictionary<(uint Begin, uint End, uint Unwind), int>();
            uint previousBegin = 0;
            for (int i = 0; i < functions.Length; i++)
            {
                int recordOffset = table.Offset + i * RUNTIME_FUNCTION_SIZE;
                uint begin = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(recordOffset, 4));
                uint end = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(recordOffset + 4, 4));
                uint unwind = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(recordOffset + 8, 4));
                if (begin == 0 || end <= begin)
                    throw new InvalidDataException($"RUNTIME_FUNCTION record {i} has an invalid range.");
                if (i > 0 && begin <= previousBegin)
                    throw new InvalidDataException("RUNTIME_FUNCTION entries are unsorted or have duplicate starts.");

                NativeMethodRange range = MapRvaRange(sections, begin, end - begin, executable: true,
                    $"RUNTIME_FUNCTION record {i}");
                functions[i] = new RuntimeFunction(begin, end, unwind, range);
                entryIndices.Add((begin, end, unwind), i);
                previousBegin = begin;
            }

            for (int i = 1; i < functions.Length; i++)
                if (functions[i - 1].End > functions[i].Begin)
                    throw new InvalidDataException("The PE exception directory contains overlapping function RVAs.");
            RuntimeFunction[] rawOrdered = functions.OrderBy(function => function.Range.Offset).ToArray();
            for (int i = 1; i < rawOrdered.Length; i++)
            {
                if ((long)rawOrdered[i - 1].Range.Offset + rawOrdered[i - 1].Range.Length > rawOrdered[i].Range.Offset)
                    throw new InvalidDataException("The PE exception directory contains overlapping function ranges.");
            }

            for (int i = 0; i < functions.Length; i++)
                functions[i].ParentIndex = ReadChainParent(image, sections, functions[i], entryIndices);
            int[] roots = ResolveRoots(functions);
            List<NativeMethodRange> ranges = new List<NativeMethodRange>();
            foreach (IGrouping<int, int> group in Enumerable.Range(0, functions.Length).GroupBy(index => roots[index]))
            {
                RuntimeFunction root = functions[group.Key];
                RuntimeFunction[] fragments = group.Select(index => functions[index]).OrderBy(function => function.Begin).ToArray();
                bool contiguous = fragments[0].Begin == root.Begin;
                for (int i = 1; i < fragments.Length; i++)
                    contiguous &= fragments[i - 1].End == fragments[i].Begin &&
                        (long)fragments[i - 1].Range.Offset + fragments[i - 1].Range.Length == fragments[i].Range.Offset;
                if (!contiguous)
                {
                    _rangeErrors.Add(root.Range.Offset, "The native function has disjoint chained unwind fragments; a contiguous method search is unsafe.");
                    continue;
                }

                NativeMethodRange combined = MapRvaRange(sections, root.Begin, fragments[^1].End - root.Begin,
                    executable: true, "chained native function");
                ranges.Add(new NativeMethodRange(combined.Offset, combined.Length, fragments.Length));
            }
            _ranges = ranges.OrderBy(range => range.Offset).ToArray();
        }

        /// <summary>
        /// Returns a range only when rawStart is an exact root RUNTIME_FUNCTION entry start.
        /// Contiguous fragments linked by UNW_FLAG_CHAININFO are part of that root.
        /// Leaf functions without unwind entries are deliberately unsupported.
        /// </summary>
        public NativeMethodRange GetMethodRange(int rawStart)
        {
            if (rawStart < 0 || rawStart >= _imageLength)
                throw new ArgumentOutOfRangeException(nameof(rawStart), "The method start is outside the image.");
            if (_rangeErrors.TryGetValue(rawStart, out string? rangeError))
                throw new InvalidDataException(rangeError);

            int low = 0;
            int high = _ranges.Length - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                NativeMethodRange candidate = _ranges[middle];
                if (candidate.Offset == rawStart)
                    return candidate;
                if (candidate.Offset < rawStart)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            throw new InvalidDataException($"No exact PE runtime function entry exists at raw offset 0x{rawStart:X}.");
        }

        private static int? ReadChainParent(byte[] image, SectionRange[] sections, RuntimeFunction function,
            Dictionary<(uint Begin, uint End, uint Unwind), int> entryIndices)
        {
            if (function.Unwind == 0 || (function.Unwind & 3) != 0)
                throw new InvalidDataException("An UNWIND_INFO RVA is missing or not DWORD aligned.");
            NativeMethodRange header = MapRvaRange(sections, function.Unwind, 4, executable: null, "UNWIND_INFO header");
            byte versionAndFlags = image[header.Offset];
            int version = versionAndFlags & 7;
            int flags = versionAndFlags >> 3;
            if ((version != 1 && version != 2) || (flags & ~7) != 0 || ((flags & 4) != 0 && (flags & 3) != 0))
                throw new InvalidDataException("An UNWIND_INFO version or flag combination is unsupported.");
            int codeCount = image[header.Offset + 2];
            int alignedSize = (4 + codeCount * 2 + 3) & ~3;
            uint totalSize = (uint)(alignedSize + ((flags & 4) != 0 ? RUNTIME_FUNCTION_SIZE : (flags & 3) != 0 ? 4 : 0));
            NativeMethodRange info = MapRvaRange(sections, function.Unwind, totalSize, executable: null, "UNWIND_INFO record");
            if ((flags & 4) == 0)
                return null;

            int chainOffset = info.Offset + alignedSize;
            (uint Begin, uint End, uint Unwind) parent = (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(chainOffset, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(chainOffset + 4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(chainOffset + 8, 4)));
            if (!entryIndices.TryGetValue(parent, out int parentIndex))
                throw new InvalidDataException("A chained UNWIND_INFO record does not identify an exact RUNTIME_FUNCTION entry.");
            return parentIndex;
        }

        private static int[] ResolveRoots(RuntimeFunction[] functions)
        {
            int[] roots = Enumerable.Repeat(-1, functions.Length).ToArray();
            for (int i = 0; i < functions.Length; i++)
            {
                if (roots[i] >= 0)
                    continue;
                List<int> path = new List<int>();
                HashSet<int> visited = new HashSet<int>();
                int current = i;
                while (roots[current] < 0)
                {
                    if (!visited.Add(current))
                        throw new InvalidDataException("The chained UNWIND_INFO records contain a cycle.");
                    path.Add(current);
                    if (functions[current].ParentIndex is not int parent)
                        break;
                    current = parent;
                }
                int root = roots[current] >= 0 ? roots[current] : current;
                foreach (int index in path)
                    roots[index] = root;
            }
            return roots;
        }

        private static NativeMethodRange MapRvaRange(SectionRange[] sections, uint rva, uint length,
            bool? executable, string description)
        {
            SectionRange? match = null;
            long rangeEnd = (long)rva + length;
            foreach (SectionRange section in sections)
            {
                if (rva < section.VirtualStart || rangeEnd > section.VirtualEnd)
                    continue;
                if (match != null)
                    throw new InvalidDataException($"The {description} maps to multiple PE sections.");
                match = section;
            }

            if (length == 0 || match == null)
                throw new InvalidDataException($"The {description} is not contained in a single PE section.");
            if (executable != null && match.Executable != executable.Value)
                throw new InvalidDataException(executable.Value
                    ? $"The {description} is not in an executable PE section."
                    : $"The {description} must be in a non-executable PE section.");

            long sectionOffset = (long)rva - match.VirtualStart;
            if (sectionOffset + length > match.RawLength)
                throw new InvalidDataException($"The {description} contains bytes without file backing.");
            long rawOffset = match.RawOffset + sectionOffset;
            if (rawOffset > int.MaxValue || length > int.MaxValue)
                throw new InvalidDataException($"The {description} cannot be represented as a file byte range.");

            return new NativeMethodRange((int)rawOffset, (int)length);
        }

        private sealed class RuntimeFunction
        {
            public uint Begin { get; }
            public uint End { get; }
            public uint Unwind { get; }
            public NativeMethodRange Range { get; }
            public int? ParentIndex { get; set; }

            public RuntimeFunction(uint begin, uint end, uint unwind, NativeMethodRange range)
            {
                Begin = begin;
                End = end;
                Unwind = unwind;
                Range = range;
            }
        }

        private static void ValidateSectionLayout(SectionRange[] sections)
        {
            for (int i = 0; i < sections.Length; i++)
            {
                for (int j = i + 1; j < sections.Length; j++)
                {
                    SectionRange first = sections[i];
                    SectionRange second = sections[j];
                    if (first.MappedLength > 0 && second.MappedLength > 0 &&
                        first.VirtualStart < second.VirtualStart + second.MappedLength &&
                        second.VirtualStart < first.VirtualStart + first.MappedLength)
                        throw new InvalidDataException("PE sections overlap in virtual address space.");
                    if (first.RawLength > 0 && second.RawLength > 0 &&
                        first.RawOffset < (long)second.RawOffset + second.RawLength &&
                        second.RawOffset < (long)first.RawOffset + first.RawLength)
                        throw new InvalidDataException("PE sections overlap in file byte ranges.");
                }
            }
        }

        private sealed class SectionRange
        {
            public long VirtualStart { get; }
            public long VirtualEnd { get; }
            public long MappedLength { get; }
            public int RawOffset { get; }
            public int RawLength { get; }
            public bool Executable { get; }

            public SectionRange(SectionHeader section, PEHeader header, int imageLength)
            {
                if (section.VirtualAddress < 0 || section.VirtualSize < 0 ||
                    section.PointerToRawData < 0 || section.SizeOfRawData < 0)
                    throw new InvalidDataException("A PE section has negative address or size values.");

                VirtualStart = section.VirtualAddress;
                // A zero VirtualSize uses the raw size; raw alignment padding is otherwise excluded.
                long virtualLength = section.VirtualSize == 0 ? section.SizeOfRawData : section.VirtualSize;
                VirtualEnd = VirtualStart + virtualLength;
                MappedLength = Math.Max(section.VirtualSize, section.SizeOfRawData);
                RawOffset = section.PointerToRawData;
                RawLength = section.SizeOfRawData;
                Executable = (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0;

                if (MappedLength > 0 && (VirtualStart < header.SizeOfHeaders ||
                    VirtualStart + MappedLength > header.SizeOfImage))
                    throw new InvalidDataException("A PE section is outside the declared virtual image.");
                if (RawLength > 0 && (RawOffset < header.SizeOfHeaders ||
                    (long)RawOffset + RawLength > imageLength))
                    throw new InvalidDataException("A PE section is outside the file or overlaps its headers.");
            }
        }
    }
}
