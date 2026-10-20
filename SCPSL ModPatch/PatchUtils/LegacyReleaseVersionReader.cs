using Iced.Intel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>
    /// Reads pre-GameCore release identities from bounded methods and their matching metadata.
    /// </summary>
    internal static class LegacyReleaseVersionReader
    {
        internal sealed record Method(string Name, ulong Rva, NativeMethodRange Range);

        internal sealed class Result
        {
            public string Method { get; } = "CustomNetworkManager$$.cctor -> Misc$$ParseVersion";
            public string Reader { get; internal set; } = "LegacyCompatibleVersions";
            public string? VerifiedVersion { get; internal set; }
            public byte? VersionTypeByte { get; internal set; }
            public string? ReleaseLabel { get; internal set; }
            public string? ReleaseQualifier { get; internal set; }
            public string? NetworkVersionTuple { get; internal set; }
            public string Status { get; internal set; } = "Unknown";
            public object? Location { get; internal set; }
            public object? LegacyEvidence { get; internal set; }
        }

        /// <summary>
        /// Reads only qualified native layouts; absent, ambiguous or malformed evidence throws.
        /// </summary>
        internal static Result Read(byte[] image, IReadOnlyDictionary<ulong, List<string>> labels,
            IReadOnlyDictionary<ulong, string> strings, ISet<ulong> arrayFormatTargets,
            Func<string, Method> resolve, string[] networkMetadataFields, Result? evidence = null)
        {
            Result report = evidence ?? new Result();
            Method constructor = resolve("CustomNetworkManager$$.cctor");
            Method parser = resolve("Misc$$ParseVersion");
            DecodeResult constructorDecoded = Decode(image, constructor);
            DecodeResult parserDecoded = Decode(image, parser);
            if (constructorDecoded.Error != null || parserDecoded.Error != null)
                throw new InvalidDataException("Legacy version methods did not decode completely.");
            ulong[] typeAddresses = labels.Where(pair => pair.Value.Contains("CustomNetworkManager_TypeInfo", StringComparer.Ordinal)).Select(pair => pair.Key).ToArray();
            if (typeAddresses.Length != 1)
                throw new InvalidDataException("Legacy network TypeInfo label is absent or ambiguous.");
            ulong networkType = typeAddresses[0];

            // The constructor stores the string array's first element, then publishes that same RBX array as CompatibleVersions.
            int firstElement = RequireAnchor(image, constructor, constructorDecoded, "48 8D 4B 20 49 8B D0 4C 89 01");
            int? laterPublish = OptionalAnchor(image, constructor, constructorDecoded, "48 8B 88 B8 00 00 00 48 83 C1 40 48 89 19");
            int? labelPublish = OptionalAnchor(image, constructor, constructorDecoded, "48 8B 88 B8 00 00 00 48 83 C1 28 48 89 19");
            if (laterPublish.HasValue == labelPublish.HasValue)
                throw new InvalidDataException("No unique supported CompatibleVersions field layout.");
            bool labelRevision = labelPublish.HasValue;
            if (labelRevision && (networkMetadataFields.Contains("SyncedRevision", StringComparer.Ordinal) ||
                !new[] { "CompatibleVersions", "Major", "Minor", "isPrivateBeta", "isStreamingAllowed" }.All(name => networkMetadataFields.Contains(name, StringComparer.Ordinal))))
                throw new InvalidDataException("The earlier label layout lacks its required metadata fields or contains a separate SyncedRevision field.");
            int publish = (laterPublish ?? labelPublish)!.Value;
            if (publish <= firstElement || publish - firstElement > 64)
                throw new InvalidDataException("CompatibleVersions array publication does not follow its first element store.");
            int publishIndex = constructorDecoded.Instructions.FindIndex(item => item.Offset == publish);
            if (publishIndex < 2 || !IsNetworkReference(constructorDecoded.Instructions[publishIndex - 2], Register.RAX, networkType) ||
                constructorDecoded.Instructions[publishIndex - 1].Bytes != "48 8B D3")
                throw new InvalidDataException("CompatibleVersions publication is not bound to the network static fields and RBX array.");
            var literalReferences = constructorDecoded.Instructions.Where(item => item.Offset < firstElement && item.Offset >= firstElement - 96 &&
                item.Instruction.Mnemonic == Mnemonic.Mov && item.Instruction.Op0Register == Register.R8 && item.Instruction.IsIPRelativeMemoryOperand &&
                strings.ContainsKey(item.Instruction.IPRelativeMemoryAddress)).Select(item => new
                {
                    item.Offset,
                    Address = item.Instruction.IPRelativeMemoryAddress,
                    Value = strings[item.Instruction.IPRelativeMemoryAddress]
                }).GroupBy(item => item.Address).Select(group => group.First()).ToArray();
            if (literalReferences.Length != 1)
                throw new InvalidDataException("The first CompatibleVersions element does not reference exactly one metadata string.");
            var literal = literalReferences[0];
            string[] components = literal.Value.Split('.');
            if (components.Length < 2 || !byte.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte major) || !byte.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte minor) ||
                major == 0 || major > 99 || minor > 99 || (labelRevision && (components.Length != 3 || !byte.TryParse(components[2], NumberStyles.None, CultureInfo.InvariantCulture, out _))))
                throw new InvalidDataException("CompatibleVersions[0] does not contain the required numeric components for this proven reader.");
            bool earlyNetworkTuple = !labelRevision && !HasByteStore(constructorDecoded, 0x3C) && HasByteStore(constructorDecoded, 0x3B);
            if (earlyNetworkTuple)
            {
                string streamingField = networkMetadataFields.Contains("StreamingAllowed", StringComparer.Ordinal) ? "StreamingAllowed" : "isStreamingAllowed";
                string[] expectedFields = { "Modded", "PrivateBeta", streamingField, "PublicBeta", "CompatibleVersions", "SyncedRevision" };
                int firstField = Array.IndexOf(networkMetadataFields, expectedFields[0]);
                if (firstField < 0 || !networkMetadataFields.Skip(firstField).Take(expectedFields.Length).SequenceEqual(expectedFields))
                    throw new InvalidDataException("Earlier network flag layout does not match the bound metadata field sequence.");
            }
            Store? revision = labelRevision ? null : ReadNetworkByte(constructorDecoded, networkType, 0x48);
            Store privateBeta = ReadNetworkByte(constructorDecoded, networkType, labelRevision ? 0x21 : earlyNetworkTuple ? 0x39 : 0x3A);
            Store? publicBeta = labelRevision ? null : ReadNetworkByte(constructorDecoded, networkType, earlyNetworkTuple ? 0x3B : 0x3C);
            Store? streaming = labelRevision ? ReadNetworkByte(constructorDecoded, networkType, 0x22) : earlyNetworkTuple ? ReadNetworkByte(constructorDecoded, networkType, 0x3A) : null;
            if (revision?.Value > 99 || privateBeta.Value > 1 || publicBeta?.Value > 1 || streaming?.Value > 1)
                throw new InvalidDataException("Legacy revision or beta flags are implausible.");
            int outputs = RequireAnchor(image, constructor, constructorDecoded, labelRevision ? "48 8D 51 35 48 83 C1 34" : "48 8D 51 51 48 83 C1 50");
            int outputsIndex = constructorDecoded.Instructions.FindIndex(item => item.Offset == outputs);
            if (outputsIndex < 3 || constructorDecoded.Instructions[outputsIndex - 1].Bytes != "48 8B 88 B8 00 00 00" ||
                !constructorDecoded.Instructions.Skip(Math.Max(0, outputsIndex - 4)).Take(3).Any(item => IsNetworkReference(item, Register.RAX, networkType)))
                throw new InvalidDataException("Major/minor output addresses are not bound to the network static fields.");
            Decoded[] tails = constructorDecoded.Instructions.Where(item => item.Instruction.FlowControl == FlowControl.UnconditionalBranch &&
                item.Instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64 && item.Instruction.NearBranchTarget == parser.Rva).ToArray();
            if (tails.Length != 1 || tails[0].Offset <= outputs || tails[0].Offset - outputs > 32)
                throw new InvalidDataException("The network constructor does not tail-call the mapped ParseVersion with those output fields.");

            // ParseVersion must consume CompatibleVersions[0], split on '.', and pass elements 0/1 to the original output pointers.
            int statics = RequireAnchor(image, parser, parserDecoded, labelRevision ? "48 8B 80 B8 00 00 00 48 8B 48 28" : "48 8B 80 B8 00 00 00 48 8B 48 40");
            if (!parserDecoded.Instructions.Any(item => item.Offset < statics && item.Offset >= statics - 64 && IsNetworkReference(item, Register.RAX, networkType)))
                throw new InvalidDataException("ParseVersion's CompatibleVersions load is not bound to network TypeInfo.");
            int firstRead = RequireAnchor(image, parser, parserDecoded, "48 8B 59 20");
            int separator = RequireAnchor(image, parser, parserDecoded, "B9 2E 00 00 00 66 89 48 20");
            int preserveOutputs = RequireAnchor(image, parser, parserDecoded, "48 8B FA 48 8B F1");
            int majorParse = RequireAnchor(image, parser, parserDecoded, "45 33 C0 48 8B D6 48 8B 4B 20");
            int minorParse = RequireAnchor(image, parser, parserDecoded, "45 33 C0 48 8B D7 48 8B 4B 28");
            Decoded[] splitCalls = parserDecoded.Instructions.Where(item => IsCallNamed(item, "System.String$$Split", labels)).ToArray();
            if (splitCalls.Length != 1 || !(statics < firstRead && firstRead < separator && separator < splitCalls[0].Offset && splitCalls[0].Offset < majorParse &&
                majorParse < minorParse && preserveOutputs < statics))
                throw new InvalidDataException("Legacy string split and indexed parse operations do not appear in the required order.");
            int splitIndex = parserDecoded.Instructions.IndexOf(splitCalls[0]);
            if (splitIndex + 1 >= parserDecoded.Instructions.Count || parserDecoded.Instructions[splitIndex + 1].Bytes != "48 8B D8")
                throw new InvalidDataException("Split result is not the RBX array consumed by both parses.");
            foreach (int anchor in new[] { majorParse, minorParse })
            {
                int callOffset = anchor + BytePattern.Parse(anchor == majorParse ? "45 33 C0 48 8B D6 48 8B 4B 20" : "45 33 C0 48 8B D7 48 8B 4B 28").Length;
                if (!parserDecoded.Instructions.Any(item => item.Offset == callOffset && IsCallNamed(item, "System.Byte$$TryParse", labels)))
                    throw new InvalidDataException("An indexed version component is not passed directly to the mapped Byte.TryParse.");
            }
            report.VerifiedVersion = earlyNetworkTuple ? null : labelRevision ? literal.Value : $"{major}.{minor}.{revision!.Value}";
            report.VersionTypeByte = null;
            report.Location = new
            {
                Constructor = Describe(constructor),
                Parser = Describe(parser)
            };
            if (earlyNetworkTuple)
            {
                report.Reader = "LegacyCompatibleVersionsReleaseDisplay";
                Decoded[] releaseLoads = constructorDecoded.Instructions.Where(item => item.Offset < firstElement && item.Offset >= firstElement - 96 &&
                    item.Instruction.Mnemonic == Mnemonic.Mov && item.Instruction.Op0Register == Register.R8 &&
                    item.Instruction.IsIPRelativeMemoryOperand && item.Instruction.IPRelativeMemoryAddress == literal.Address).ToArray();
                if (releaseLoads.Length != 2)
                    throw new InvalidDataException("The qualified constructor requires both initial and post-cast loads of the same release label.");
                RequirePreservedRegister(constructorDecoded, releaseLoads[^1].Offset, firstElement + 10, Register.R8);
                int arrayOrigin = RequireAnchor(image, constructor, constructorDecoded, "48 8B D8 48 85 C0");
                RequirePreservedRegister(constructorDecoded, arrayOrigin, publish + 14, Register.RBX);
                RequireKnownObjectWrites(constructorDecoded, firstElement + 10, publish + 14, new HashSet<int> { publish + 11 });
                object releaseDisplay = VerifyLegacyReleaseDisplay(image, labels, strings, arrayFormatTargets, resolve, networkType);
                Match releaseMatch = Regex.Match(literal.Value,
                    @"\A([0-9]{1,2})\.([0-9]{1,2})\.([0-9]{1,2})( \([^\r\n()]+\))?\z", RegexOptions.CultureInvariant);
                if (!releaseMatch.Success || !byte.TryParse(releaseMatch.Groups[3].Value, out _))
                    throw new InvalidDataException("The displayed release label has no supported numeric release prefix and optional parenthesized qualifier.");
                report.VerifiedVersion = string.Join(".", releaseMatch.Groups.Cast<Group>().Skip(1).Take(3)
                    .Select(group => byte.Parse(group.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));
                report.ReleaseLabel = literal.Value;
                report.ReleaseQualifier = releaseMatch.Groups[4].Success ? releaseMatch.Groups[4].Value.Trim() : null;
                report.NetworkVersionTuple = $"{major}.{minor}.{revision!.Value}";
                report.LegacyEvidence = new
                {
                    CompatibleVersionString = literal.Value,
                    CompatibleVersionLabel = literal.Value,
                    NetworkVersionTuple = report.NetworkVersionTuple,
                    ReleaseDisplay = releaseDisplay,
                    GameVersionInterpretation = "Release identity comes from the exact label consumed as BuildInfo's Game version argument; the separate tuple is consumed as Preauth version. Qualifiers are retained.",
                    LiteralAddress = literal.Address,
                    LiteralLoadRelativeOffset = literal.Offset,
                    ArrayFirstElementStore = firstElement,
                    CompatibleVersionsPublication = publish,
                    Revision = revision,
                    MetadataFields = networkMetadataFields,
                    PrivateBeta = privateBeta,
                    PublicBeta = publicBeta,
                    StreamingAllowed = streaming,
                    OutputPointersRelativeOffset = outputs,
                    ParseTailRelativeOffset = tails[0].Offset,
                    ParserCompatibleVersionsLoad = statics,
                    ParserSplitCall = splitCalls[0].Offset,
                    ParserMajorParse = majorParse,
                    ParserMinorParse = minorParse,
                    TypeInterpretation = "Earlier beta/streaming booleans are recorded independently; no modern enum type is inferred."
                };
                report.Status = "Release label verified through the bounded build-info Game version consumer; preauth/network revision is retained separately and never used as the release revision.";
            }
            else if (labelRevision)
            {
                report.Reader = "LegacyCompatibleVersionsLabel";
                report.LegacyEvidence = new
                {
                    CompatibleVersionString = literal.Value,
                    LiteralAddress = literal.Address,
                    LiteralLoadRelativeOffset = literal.Offset,
                    ArrayFirstElementStore = firstElement,
                    CompatibleVersionsPublication = publish,
                    CompatibleVersionsStaticDisplacement = 0x28,
                    Revision = (Store?)null,
                    RevisionOrigin = "Third numeric component of the exact first CompatibleVersions metadata label; no separate SyncedRevision field exists in this type.",
                    MetadataFields = networkMetadataFields,
                    PrivateBeta = privateBeta,
                    StreamingAllowed = streaming,
                    OutputPointersRelativeOffset = outputs,
                    ParseTailRelativeOffset = tails[0].Offset,
                    ParserCompatibleVersionsLoad = statics,
                    ParserSplitCall = splitCalls[0].Offset,
                    ParserMajorParse = majorParse,
                    ParserMinorParse = minorParse,
                    TypeInterpretation = "Earlier private-beta and streaming flags are reported independently; no modern enum type byte or public-beta flag is inferred."
                };
                report.Status = "Numeric version label verified from uniquely referenced CompatibleVersions[0], native publication, metadata absence of SyncedRevision, and bounded major/minor ParseVersion dataflow. Revision is from the label, not a separate runtime byte.";
            }
            else
            {
                report.LegacyEvidence = new
                {
                    CompatibleVersionString = literal.Value,
                    LiteralAddress = literal.Address,
                    LiteralLoadRelativeOffset = literal.Offset,
                    ArrayFirstElementStore = firstElement,
                    CompatibleVersionsPublication = publish,
                    Revision = revision,
                    PrivateBeta = privateBeta,
                    PublicBeta = publicBeta,
                    OutputPointersRelativeOffset = outputs,
                    ParseTailRelativeOffset = tails[0].Offset,
                    ParserCompatibleVersionsLoad = statics,
                    ParserSplitCall = splitCalls[0].Offset,
                    ParserMajorParse = majorParse,
                    ParserMinorParse = minorParse,
                    TypeInterpretation = "Legacy beta flags are reported independently; no modern enum type byte is inferred."
                };
                report.Status = "Version derived from uniquely referenced CompatibleVersions[0] major/minor and network revision store, with bounded constructor and ParseVersion dataflow anchors.";
            }
            return report;
        }

        private static object VerifyLegacyReleaseDisplay(byte[] image, IReadOnlyDictionary<ulong, List<string>> labels,
            IReadOnlyDictionary<ulong, string> strings, ISet<ulong> arrayFormatTargets, Func<string, Method> resolve, ulong networkType)
        {
            Method method = resolve("CommandSystem.Commands.BuildInfoCommand$$Execute");
            DecodeResult decoded = Decode(image, method);
            if (decoded.Error != null)
                throw new InvalidDataException("Build-info consumer did not decode completely.");
            int statics = RequireBuildInfoAnchor(image, method, decoded, "48 8B 89 B8 00 00 00 48 89 7C 24 48 48 8B 41 40");
            int staticIndex = decoded.Instructions.FindIndex(item => item.Offset == statics);
            if (staticIndex < 1 || !IsNetworkReference(decoded.Instructions[staticIndex - 1], Register.RCX, networkType))
                throw new InvalidDataException("Build-info CompatibleVersions load is not bound to network TypeInfo.");
            int releaseRead = RequireBuildInfoAnchor(image, method, decoded, "48 8B 78 20 48 85 DB");
            int releaseStore = RequireBuildInfoAnchor(image, method, decoded, "48 8D 4B 20 48 8B D7 48 89 39");
            int arrayAssignment = RequireBuildInfoAnchor(image, method, decoded, "48 8B D8 F6 81 2F 01 00 00 02");
            int allocation = RequireBuildInfoAnchor(image, method, decoded, "48 8B 0D ?? ?? ?? ?? BA 0C 00 00 00 C6 44 24 27 00 E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 48 8B D8");
            Decoded allocationType = decoded.Instructions.Single(item => item.Offset == allocation);
            if (!allocationType.Instruction.IsIPRelativeMemoryOperand ||
                !labels.TryGetValue(allocationType.Instruction.IPRelativeMemoryAddress, out List<string>? allocationNames) ||
                !allocationNames.Contains("object[]_TypeInfo", StringComparer.Ordinal) || arrayAssignment != allocation + 29)
                throw new InvalidDataException("Build-info format arguments are not the expected newly allocated object array.");
            if (!(arrayAssignment < statics && statics < releaseRead && releaseRead < releaseStore && releaseStore - releaseRead < 96))
                throw new InvalidDataException("Build-info does not publish the first compatible label as format argument zero.");
            RequirePreservedRegister(decoded, statics + 12, releaseRead, Register.RAX);
            RequirePreservedRegister(decoded, releaseRead, releaseStore, Register.RDI);
            List<object> preauth = new List<object>();
            foreach ((byte displacement, byte argumentOffset) in new[] { ((byte)0x50, (byte)0x28), ((byte)0x51, (byte)0x30), ((byte)0x48, (byte)0x38) })
            {
                int read = RequireBuildInfoAnchor(image, method, decoded, $"0F B6 41 {displacement:X2}");
                int index = decoded.Instructions.FindIndex(item => item.Offset == read);
                if (index < 3 || !IsNetworkReference(decoded.Instructions[index - 3], Register.RAX, networkType) ||
                    decoded.Instructions[index - 1].Bytes != "48 8B 88 B8 00 00 00")
                    throw new InvalidDataException("Build-info preauth component is not bound to network static fields.");
                int store = RequireBuildInfoAnchor(image, method, decoded, $"48 8D 4B {argumentOffset:X2} 48 8B D7 48 89 39");
                if (store <= read || store - read > 96)
                    throw new InvalidDataException("Build-info preauth argument store is not adjacent to its source.");
                if (index + 4 >= decoded.Instructions.Count)
                    throw new InvalidDataException("Build-info preauth boxing is truncated.");
                Instruction pointer = decoded.Instructions[index - 2].Instruction;
                Instruction boxType = decoded.Instructions[index + 1].Instruction;
                Instruction valueStore = decoded.Instructions[index + 2].Instruction;
                if (pointer.Mnemonic != Mnemonic.Lea || pointer.Op0Register != Register.RDX || pointer.MemoryBase != Register.RSP ||
                    boxType.Mnemonic != Mnemonic.Mov || boxType.Op0Register != Register.RCX || !boxType.IsIPRelativeMemoryOperand ||
                    !labels.TryGetValue(boxType.IPRelativeMemoryAddress, out List<string>? boxNames) || !boxNames.Contains("byte_TypeInfo", StringComparer.Ordinal) ||
                    valueStore.Mnemonic != Mnemonic.Mov || valueStore.Op0Kind != OpKind.Memory || valueStore.MemoryBase != Register.RSP ||
                    valueStore.MemoryDisplacement64 != pointer.MemoryDisplacement64 || valueStore.Op1Register != Register.AL ||
                    decoded.Instructions[index + 3].Instruction.FlowControl != FlowControl.Call || decoded.Instructions[index + 4].Bytes != "48 8B F8")
                    throw new InvalidDataException("Build-info does not box the selected network byte into its preauth argument.");
                RequirePreservedRegister(decoded, decoded.Instructions[index + 4].Offset, store, Register.RDI);
                preauth.Add(new
                {
                    StaticDisplacement = displacement,
                    ArgumentIndex = (argumentOffset - 0x20) / 8,
                    ReadOffset = read,
                    StoreOffset = store
                });
            }
            Decoded[] formats = decoded.Instructions.Where(item => item.Instruction.Mnemonic == Mnemonic.Mov && item.Instruction.Op0Register == Register.RSI &&
                item.Instruction.IsIPRelativeMemoryOperand && strings.TryGetValue(item.Instruction.IPRelativeMemoryAddress, out string? value) &&
                value.StartsWith("Build info:\nGame version: {0}\nPreauth version: {1}.{2}.{3}\n", StringComparison.Ordinal)).ToArray();
            if (formats.Length != 1)
                throw new InvalidDataException("Build-info has no unique explicit game/preauth format literal.");
            Decoded format = formats[0];
            int arguments = RequireBuildInfoAnchor(image, method, decoded, "45 33 C0 48 8B D3 48 8B CE");
            int callOffset = arguments + 9;
            if (format.Offset <= releaseStore || arguments <= format.Offset || arrayFormatTargets.Count != 1 ||
                !decoded.Instructions.Any(item => item.Offset == callOffset && IsCallNamed(item, "System.String$$Format", labels) && arrayFormatTargets.Contains(item.Instruction.NearBranchTarget)))
                throw new InvalidDataException("Build-info does not pass the proven label and format to the mapped String.Format.");
            RequirePreservedRegister(decoded, arrayAssignment, arguments, Register.RBX);
            RequirePreservedRegister(decoded, format.Offset, arguments, Register.RSI);
            HashSet<int> knownStores = new HashSet<int>();
            for (int argumentOffset = 0x28; argumentOffset < 0x80; argumentOffset += 8)
            {
                BytePattern bytes = BytePattern.Parse($"48 8D 4B {argumentOffset:X2} 48 8B D7 48 89 39");
                // A present argument store must be unique; some builders leave slots unused.
                ReadOnlySpan<byte> body = image.AsSpan(method.Range.Offset, method.Range.Length);
                bool present = false;
                for (int i = 0; i <= body.Length - bytes.Length; i++)
                {
                    if (body.Slice(i, bytes.Length).SequenceEqual(Convert.FromHexString($"488D4B{argumentOffset:X2}488BD7488939")))
                    {
                        present = true;
                        break;
                    }
                }
                if (present)
                    knownStores.Add(RequireBuildInfoAnchor(image, method, decoded, $"48 8D 4B {argumentOffset:X2} 48 8B D7 48 89 39") + 7);
            }
            RequireKnownObjectWrites(decoded, releaseStore + 10, callOffset, knownStores);
            return new
            {
                Method = Describe(method),
                CodeSha256 = Hash(image.AsSpan(method.Range.Offset, method.Range.Length).ToArray()),
                CompatibleVersionsStaticDisplacement = 0x40,
                CompatibleVersionsLoadOffset = statics,
                FormatArrayAllocationOffset = allocation,
                FormatArrayLength = 12,
                ReleaseReadOffset = releaseRead,
                ReleaseArgumentStoreOffset = releaseStore,
                ReleaseArgumentIndex = 0,
                PreauthArguments = preauth,
                FormatAddress = format.Instruction.IPRelativeMemoryAddress,
                FormatLiteral = strings[format.Instruction.IPRelativeMemoryAddress],
                FormatLoadOffset = format.Offset,
                StringFormatCallOffset = callOffset,
                StringFormatTargetRva = arrayFormatTargets.Single(),
                StringFormatOverload = "string Format(string format, object[] args)",
                Scope = "Qualified bounded build-info layout; exact array/field anchors and preserved release/format registers. No general CFG proof or runtime execution."
            };
        }

        private static int RequireAnchor(byte[] image, Method method, DecodeResult decoded, string pattern)
        {
            return RequireAnchor(image, method, decoded, pattern, "Legacy reader anchor is not bounded by decoded instructions.");
        }

        private static int RequireBuildInfoAnchor(byte[] image, Method method, DecodeResult decoded, string pattern)
        {
            return RequireAnchor(image, method, decoded, pattern, "Build-info anchor is not bounded by instructions.");
        }

        private static int RequireAnchor(byte[] image, Method method, DecodeResult decoded, string pattern, string errorMessage)
        {
            BytePattern bytes = BytePattern.Parse(pattern);
            int offset = bytes.FindUniqueOffset(image.AsSpan(method.Range.Offset, method.Range.Length));
            HashSet<int> boundaries = decoded.Instructions.Select(item => item.Offset).Append(method.Range.Length).ToHashSet();
            if (!boundaries.Contains(offset) || !boundaries.Contains(offset + bytes.Length))
                throw new InvalidDataException(errorMessage);
            return offset;
        }

        private static int? OptionalAnchor(byte[] image, Method method, DecodeResult decoded, string pattern)
        {
            byte[] bytes = Convert.FromHexString(pattern.Replace(" ", ""));
            ReadOnlySpan<byte> body = image.AsSpan(method.Range.Offset, method.Range.Length);
            int count = 0;
            for (int offset = 0; offset <= body.Length - bytes.Length; offset++)
            {
                if (body.Slice(offset, bytes.Length).SequenceEqual(bytes))
                    count++;
            }
            if (count > 1)
                throw new InvalidDataException("An optional legacy layout anchor is ambiguous.");
            return count == 0 ? null : RequireAnchor(image, method, decoded, pattern);
        }

        private static bool IsNetworkReference(Decoded item, Register destination, ulong networkType)
        {
            return item.Instruction.Mnemonic == Mnemonic.Mov && item.Instruction.Op0Register == destination &&
                item.Instruction.IsIPRelativeMemoryOperand && item.Instruction.IPRelativeMemoryAddress == networkType;
        }

        private static Store ReadNetworkByte(DecodeResult decoded, ulong networkType, int displacement)
        {
            Decoded[] candidates = decoded.Instructions.Where(item => item.Instruction.Mnemonic == Mnemonic.Mov && item.Instruction.Op0Kind == OpKind.Memory &&
                item.Instruction.MemoryBase == Register.RCX && item.Instruction.MemoryIndex == Register.None && item.Instruction.MemoryDisplacement64 == (ulong)displacement &&
                item.Instruction.MemorySize == MemorySize.UInt8 && item.Instruction.Op1Kind == OpKind.Immediate8).ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException($"Legacy network field +0x{displacement:X} has no unique immediate byte store.");
            Decoded store = candidates[0];
            int index = decoded.Instructions.IndexOf(store);
            if (index < 2 || !IsNetworkReference(decoded.Instructions[index - 2], Register.RAX, networkType) || decoded.Instructions[index - 1].Bytes != "48 8B 88 B8 00 00 00")
                throw new InvalidDataException("Legacy byte store is not bound to network static fields.");
            return new Store(store.Offset, store.Offset + store.Instruction.Length - 1, "RCX", displacement, store.Instruction.Immediate8, store.Bytes);
        }

        private static bool HasByteStore(DecodeResult decoded, int displacement)
        {
            return decoded.Instructions.Any(item => item.Instruction.Mnemonic == Mnemonic.Mov &&
                item.Instruction.Op0Kind == OpKind.Memory && item.Instruction.MemoryBase == Register.RCX &&
                item.Instruction.MemoryDisplacement64 == (ulong)displacement && item.Instruction.MemorySize == MemorySize.UInt8 &&
                item.Instruction.Op1Kind == OpKind.Immediate8);
        }

        private static bool IsCallNamed(Decoded item, string name, IReadOnlyDictionary<ulong, List<string>> labels)
        {
            return item.Instruction.FlowControl == FlowControl.Call &&
                item.Instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64 &&
                labels.TryGetValue(item.Instruction.NearBranchTarget, out List<string>? names) && names.Contains(name, StringComparer.Ordinal);
        }

        private static void RequirePreservedRegister(DecodeResult decoded, int after, int before, Register register)
        {
            InstructionInfoFactory factory = new InstructionInfoFactory();
            foreach (Decoded item in decoded.Instructions.Where(item => item.Offset > after && item.Offset < before))
            {
                Instruction instruction = item.Instruction;
                if (factory.GetInfo(in instruction).GetUsedRegisters().Any(used => used.Register.GetFullRegister() == register &&
                    used.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite))
                    throw new InvalidDataException($"Version evidence overwrites {register} between its proven producer and consumer.");
            }
        }

        private static void RequireKnownObjectWrites(DecodeResult decoded, int after, int before, HashSet<int> allowedStores)
        {
            InstructionInfoFactory factory = new InstructionInfoFactory();
            foreach (Decoded item in decoded.Instructions.Where(item => item.Offset >= after && item.Offset < before))
            {
                Instruction instruction = item.Instruction;
                foreach (UsedMemory memory in factory.GetInfo(in instruction).GetUsedMemory())
                {
                    if (memory.Access is not (OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite))
                        continue;
                    if (memory.Base is Register.RSP or Register.RBP)
                        continue;
                    // IL2CPP lazy-init flags are image-relative byte writes outside the new argument array.
                    if (instruction.IsIPRelativeMemoryOperand && instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Memory &&
                        instruction.MemorySize == MemorySize.UInt8 && instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 1)
                        continue;
                    if (!allowedStores.Contains(item.Offset) || memory.Base != Register.RCX || memory.Index != Register.None || memory.Displacement != 0)
                        throw new InvalidDataException($"Version evidence contains an unqualified object write at +0x{item.Offset:X} after publishing the release label.");
                }
            }
        }

        private static DecodeResult Decode(byte[] image, Method method)
        {
            List<Decoded> instructions = new List<Decoded>();
            Iced.Intel.Decoder decoder = Iced.Intel.Decoder.Create(64, image.AsSpan(method.Range.Offset, method.Range.Length).ToArray(), method.Rva);
            IntelFormatter formatter = new IntelFormatter();
            StringOutput formatted = new StringOutput();
            while (decoder.IP < method.Rva + (ulong)method.Range.Length)
            {
                decoder.Decode(out Instruction instruction);
                if (instruction.Code == Code.INVALID || instruction.Length == 0 || decoder.IP > method.Rva + (ulong)method.Range.Length)
                    return new DecodeResult(instructions, $"Invalid or truncated instruction at +0x{instruction.IP - method.Rva:X}.");
                formatter.Format(instruction, formatted);
                int offset = checked((int)(instruction.IP - method.Rva));
                instructions.Add(new Decoded(instruction, offset, Hex(image.AsSpan(method.Range.Offset + offset, instruction.Length)), formatted.ToStringAndReset()));
            }
            return new DecodeResult(instructions, null);
        }

        private static object Describe(Method method)
        {
            return new
            {
                Method = method.Name,
                Rva = method.Rva,
                RawOffset = method.Range.Offset,
                Length = method.Range.Length,
                FragmentCount = method.Range.FragmentCount,
                BoundarySource = "Exact AMD64 runtime-function root and contiguous chained unwind fragments."
            };
        }

        private static string Hex(ReadOnlySpan<byte> bytes)
        {
            return string.Join(" ", bytes.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        }

        private static string Hash(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        private sealed record Decoded(Instruction Instruction, int Offset, string Bytes, string Text);
        private sealed record DecodeResult(List<Decoded> Instructions, string? Error);
        private sealed record Store(int InstructionOffset, int ValueOffset, string BaseRegister, int Displacement, byte Value, string Bytes);
    }
}
