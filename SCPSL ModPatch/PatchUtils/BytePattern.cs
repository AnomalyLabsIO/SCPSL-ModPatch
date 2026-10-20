using System;
using System.Collections.Generic;
using System.IO;

namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>
    /// An immutable byte pattern with optional full-byte wildcards.
    /// </summary>
    internal sealed class BytePattern
    {
        readonly byte[] _bytes;
        readonly bool[] _wildcards;
        readonly int _anchorIndex;

        public int Length { get => _bytes.Length; }

        private BytePattern(byte[] bytes, bool[] wildcards, int anchorIndex)
        {
            _bytes = bytes;
            _wildcards = wildcards;
            _anchorIndex = anchorIndex;
        }

        /// <summary>
        /// Parses hex byte pairs, optionally separated by whitespace or hyphens.
        /// A wildcard is either a ?? pair or a standalone ? token.
        /// </summary>
        public static BytePattern Parse(string pattern)
        {
            if (pattern is null)
                throw new ArgumentNullException(nameof(pattern));

            List<byte> bytes = new List<byte>();
            List<bool> wildcards = new List<bool>();
            int anchorIndex = -1;
            int position = 0;

            while (position < pattern.Length)
            {
                if (IsSeparator(pattern[position]))
                {
                    position++;
                    continue;
                }

                int tokenStart = position;
                while (position < pattern.Length && !IsSeparator(pattern[position]))
                    position++;

                int tokenLength = position - tokenStart;
                if (tokenLength == 1 && pattern[tokenStart] == '?')
                {
                    bytes.Add(0);
                    wildcards.Add(true);
                    continue;
                }

                if (tokenLength % 2 != 0)
                    throw new FormatException("Byte patterns must contain complete hex byte pairs or full-byte wildcards.");

                for (int index = tokenStart; index < position; index += 2)
                {
                    char high = pattern[index];
                    char low = pattern[index + 1];
                    if (high == '?' && low == '?')
                    {
                        bytes.Add(0);
                        wildcards.Add(true);
                        continue;
                    }

                    int highValue = HexValue(high);
                    int lowValue = HexValue(low);
                    if (highValue < 0 || lowValue < 0)
                        throw new FormatException("Byte patterns accept only hex byte pairs and full-byte wildcards (? or ??).");

                    if (anchorIndex < 0)
                        anchorIndex = bytes.Count;
                    bytes.Add((byte)((highValue << 4) | lowValue));
                    wildcards.Add(false);
                }
            }

            if (bytes.Count == 0)
                throw new FormatException("A byte pattern cannot be empty.");
            if (anchorIndex < 0)
                throw new FormatException("A byte pattern must contain at least one fixed byte.");

            return new BytePattern(bytes.ToArray(), wildcards.ToArray(), anchorIndex);
        }

        /// <summary>
        /// Returns the sole match relative to the supplied method bytes.
        /// Missing and ambiguous matches both prevent patching.
        /// </summary>
        public int FindUniqueOffset(ReadOnlySpan<byte> methodBytes)
        {
            int result = -1;
            int lastStart = methodBytes.Length - Length;
            for (int start = 0; start <= lastStart; start++)
            {
                if (methodBytes[start + _anchorIndex] != _bytes[_anchorIndex])
                    continue;

                bool matches = true;
                for (int index = 0; index < Length; index++)
                {
                    if (!_wildcards[index] && methodBytes[start + index] != _bytes[index])
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                    continue;
                if (result >= 0)
                    throw new InvalidDataException("The byte pattern matches more than once within the method; refusing to choose a patch location.");
                result = start;
            }

            if (result < 0)
                throw new InvalidDataException("The byte pattern was not found within the method.");

            return result;
        }

        private static bool IsSeparator(char character)
        {
            return character == '-' || char.IsWhiteSpace(character);
        }

        private static int HexValue(char character)
        {
            if (character >= '0' && character <= '9')
                return character - '0';
            if (character >= 'a' && character <= 'f')
                return character - 'a' + 10;
            if (character >= 'A' && character <= 'F')
                return character - 'A' + 10;
            return -1;
        }
    }
}
