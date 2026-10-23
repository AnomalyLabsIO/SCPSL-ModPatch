using System.Globalization;
using System.Security.Cryptography;

namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>
    /// Selects a complete configuration from the original DLL identity.
    /// </summary>
    public static class PatchTargetResolver
    {
        /// <summary>Infers the backend for manifests that omit it.</summary>
        public static string GetBackend(VersionRangeInfo target)
        {
            bool managed = target.methods?.patchMethods?.Any(patch => patch != null &&
                (patch.ilPattern != null || patch.ilEdit != null)) == true;
            string backend = target.backend?.ToLowerInvariant() ?? (managed ? "mono" : "il2cpp");
            if (backend != "mono" && backend != "il2cpp")
                throw new InvalidDataException($"Unknown patch backend '{target.backend}'.");
            if (backend == "il2cpp" && managed)
                throw new InvalidDataException("IL edits cannot be used by an IL2CPP profile.");
            return backend;
        }

        /// <summary>Restricts target selection to one scripting backend.</summary>
        public static PatchInfo ForBackend(PatchInfo info, string backend)
        {
            Validate(info);
            return new PatchInfo { versionRanges = info.versionRanges.Where(target => GetBackend(target) == backend).ToArray() };
        }

        private static string? GetHash(VersionRangeInfo target) => target.assemblySha256 ?? target.gameAssemblySha256;
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
                string backend = GetBackend(target);
                if (backend == "mono")
                {
                    if (target.gameAssemblySha256 != null)
                        throw new InvalidDataException("Mono profiles use assemblySha256, not gameAssemblySha256.");
                    if (target.methods.patchMethods.Any(patch => patch == null ||
                        patch.ilPattern == null || patch.ilEdit == null || patch.patchPattern != null || patch.patchOffset != null ||
                        patch.patternOffset != 0 || patch.patchSize != null || !string.IsNullOrEmpty(patch.patchData)))
                        throw new InvalidDataException("Mono profiles require IL patterns and edits, without native byte-patch fields.");
                    if (target.methods.patchMethods.Any(patch => patch.patchifexists != null && string.IsNullOrWhiteSpace(patch.patchifexists)))
                        throw new InvalidDataException("patchifexists requires a full Mono method signature.");
                }
                else if (target.assemblySha256 != null || target.assemblyPath != null)
                    throw new InvalidDataException("assemblyPath and assemblySha256 are supported by the Mono backend only.");
                else if (target.methods.patchMethods.Any(patch => patch?.patchifexists != null))
                    throw new InvalidDataException("patchifexists is supported by the Mono backend only.");
                bool hasRange = !string.IsNullOrEmpty(target.versionFrom) || !string.IsNullOrEmpty(target.versionTo);
                if (GetHash(target) != null)
                {
                    if (target.version != null || hasRange)
                        throw new InvalidDataException("A hash target cannot also specify a version or range.");
                    ValidateHash(GetHash(target));
                }
                else if (target.version != null)
                {
                    if (hasRange)
                        throw new InvalidDataException("An exact version target cannot also specify a range.");
                    if (backend == "mono")
                    {
                        if (string.IsNullOrWhiteSpace(target.version))
                            throw new InvalidDataException("An exact Mono version label cannot be empty.");
                    }
                    else
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
        public static VersionRangeInfo? ResolveHash(PatchInfo info, string originalSha256, string backend = "il2cpp")
        {
            Validate(info);
            ValidateHash(originalSha256);
            return GetSingleTarget(info.versionRanges.Where(target => GetBackend(target) == backend && GetHash(target) != null &&
                GetHash(target)!.Equals(originalSha256, StringComparison.OrdinalIgnoreCase)), "DLL hash");
        }

        /// <summary>
        /// Selects a target by DLL hash, exact version, or version range, in that order.
        /// </summary>
        public static VersionRangeInfo Resolve(PatchInfo info, string originalSha256, Version? version, string backend = "il2cpp")
            => Resolve(info, originalSha256, version, backend, null);

        /// <summary>Selects a target while retaining a Mono event's exact release label.</summary>
        public static VersionRangeInfo Resolve(PatchInfo info, string originalSha256, Version? version, string backend, string? versionLabel)
        {
            VersionRangeInfo? selected = ResolveHash(info, originalSha256, backend);
            if (selected != null)
                return selected;
            selected = GetSingleTarget(info.versionRanges.Where(target => GetBackend(target) == backend && target.version != null &&
                (backend == "mono" ? string.Equals(target.version, versionLabel, StringComparison.Ordinal) ||
                    version != null && Version.TryParse(target.version, out Version? exact) && exact == version :
                    ParseVersion(target.version) == version)), "exact version");
            if (selected != null)
                return selected;
            if (version == null)
                throw new InvalidDataException("The game version could not be detected and no DLL hash or exact label target matches.");

            selected = GetSingleTarget(info.versionRanges.Where(target => GetBackend(target) == backend && target.version == null && GetHash(target) == null &&
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
