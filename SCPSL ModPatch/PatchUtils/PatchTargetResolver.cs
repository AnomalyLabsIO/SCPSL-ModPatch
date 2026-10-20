using System.Globalization;
using System.Security.Cryptography;

namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>
    /// Selects a complete configuration from the original DLL identity.
    /// </summary>
    public static class PatchTargetResolver
    {
        /// <summary>
        /// Gets the SHA-256 hash of the original GameAssembly data.
        /// </summary>
        public static string ComputeSha256(byte[] originalImage)
        {
            return Convert.ToHexString(SHA256.HashData(originalImage));
        }

        /// <summary>
        /// Checks the target selectors in the patch information.
        /// </summary>
        public static void Validate(PatchInfo info)
        {
            if (info?.versionRanges == null)
                throw new InvalidDataException("Patch info must contain a versionRanges array.");
            foreach (VersionRangeInfo target in info.versionRanges)
            {
                if (target == null || target.methods?.patchMethods == null)
                    throw new InvalidDataException("Each patch target must contain methods.patchMethods.");
                bool hasRange = !string.IsNullOrEmpty(target.versionFrom) || !string.IsNullOrEmpty(target.versionTo);
                if (target.gameAssemblySha256 != null)
                {
                    if (target.version != null || hasRange)
                        throw new InvalidDataException("A hash target cannot also specify a version or range.");
                    ValidateHash(target.gameAssemblySha256);
                }
                else if (target.version != null)
                {
                    if (hasRange)
                        throw new InvalidDataException("An exact version target cannot also specify a range.");
                    ParseVersion(target.version);
                }
                else
                {
                    Version versionFrom = GetRangeStart(target);
                    if (!IsRangeUnbounded(target.versionTo) && ParseVersion(target.versionTo) < versionFrom)
                        throw new InvalidDataException("A version range cannot end before it starts.");
                }
            }
        }

        /// <summary>
        /// Gets the patch target matching the original GameAssembly hash.
        /// </summary>
        public static VersionRangeInfo? ResolveHash(PatchInfo info, string originalSha256)
        {
            Validate(info);
            ValidateHash(originalSha256);
            return GetSingleTarget(info.versionRanges.Where(target => target.gameAssemblySha256 != null &&
                target.gameAssemblySha256.Equals(originalSha256, StringComparison.OrdinalIgnoreCase)), "DLL hash");
        }

        /// <summary>
        /// Selects a target by DLL hash, exact version, or version range, in that order.
        /// </summary>
        public static VersionRangeInfo Resolve(PatchInfo info, string originalSha256, Version? version)
        {
            VersionRangeInfo? selected = ResolveHash(info, originalSha256);
            if (selected != null)
                return selected;
            if (version == null)
                throw new InvalidDataException("The game version could not be detected and no DLL hash target matches.");

            selected = GetSingleTarget(info.versionRanges.Where(target => target.version != null &&
                ParseVersion(target.version) == version), "exact version");
            if (selected != null)
                return selected;

            selected = GetSingleTarget(info.versionRanges.Where(target => target.version == null && target.gameAssemblySha256 == null &&
                version >= GetRangeStart(target) &&
                (IsRangeUnbounded(target.versionTo) || version <= ParseVersion(target.versionTo))), "version range");
            return selected ?? throw new InvalidDataException($"No patch target matches game version {version}.");
        }

        private static VersionRangeInfo? GetSingleTarget(IEnumerable<VersionRangeInfo> targets, string tier)
        {
            VersionRangeInfo[] matches = targets.Take(2).ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException($"More than one patch target matches the {tier} tier.");
            return matches.SingleOrDefault();
        }

        private static bool IsRangeUnbounded(string? value)
        {
            return string.IsNullOrEmpty(value) || value.Equals("Current", StringComparison.OrdinalIgnoreCase);
        }

        private static Version GetRangeStart(VersionRangeInfo target)
        {
            // Older custom manifests may provide only a display label for a range.
            if (string.IsNullOrEmpty(target.versionFrom) && !string.IsNullOrEmpty(target.versionRange))
                return new Version(0, 0, 0);
            return ParseVersion(target.versionFrom);
        }

        private static Version ParseVersion(string? value)
        {
            string[] parts = value?.Split('.') ?? Array.Empty<string>();
            if (parts.Length != 3 || parts.Any(part => part.Length == 0 ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                throw new InvalidDataException($"Invalid target version '{value}'; expected three nonnegative numbers, such as 10.1.1.");
            return new Version(value!);
        }

        private static void ValidateHash(string? value)
        {
            if (value == null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("A GameAssembly SHA-256 must contain exactly 64 hexadecimal characters.");
        }
    }
}
