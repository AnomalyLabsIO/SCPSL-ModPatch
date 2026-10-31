using SCPSL_ModPatch.IL2Cpp;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>
    /// Loads and verifies a patch configuration for the original game files.
    /// </summary>
    internal sealed class PatchSession
    {
        public byte[] AssemblyData { get; }
        public Il2cppManager Manager { get; }
        public VersionRangeInfo Target { get; }
        public GameVersion? DetectedVersion { get; }
        public string MetadataPath { get; }
        public string OriginalMetadataSha256 { get; }
        public byte[] NormalizedMetadata { get; }

        private PatchSession(byte[] assemblyData, Il2cppManager manager, VersionRangeInfo target,
            GameVersion? detectedVersion, string metadataPath, string originalMetadataSha256, byte[] normalizedMetadata)
        {
            AssemblyData = assemblyData;
            Manager = manager;
            Target = target;
            DetectedVersion = detectedVersion;
            MetadataPath = metadataPath;
            OriginalMetadataSha256 = originalMetadataSha256;
            NormalizedMetadata = normalizedMetadata;
        }

        /// <summary>
        /// Loads the game files and selects their complete patch configuration.
        /// </summary>
        public static async Task<PatchSession> LoadAsync(string gamePath, PatchInfo info, VersionRangeInfo? bootstrap,
            bool interactive = true, bool createDummyDlls = false)
        {
            gamePath = Path.GetFullPath(gamePath);
            PatchTargetResolver.Validate(info);
            string assemblyPath = Path.Combine(gamePath, "GameAssembly.dll");
            byte[] assemblyData = await Patcher.GetGameAssemblyDataAsync(assemblyPath).ConfigureAwait(false);
            string scratchDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SCPSL-ModPatch", Guid.NewGuid().ToString("N")));
            string dumpDirectory = Path.Combine(scratchDirectory, "dump");
            Il2cppManager manager = new Il2cppManager(assemblyData, dumpDirectory, interactive);
            string originalSha256 = manager.OriginalAssemblySha256;
            VersionRangeInfo? hashTarget = PatchTargetResolver.ResolveHash(info, originalSha256);
            bool automatic = hashTarget == null && bootstrap == null;
            string? unityVersion = automatic ? ReadUnityVersion(gamePath) : null;

            string metadataPath = Path.Combine(gamePath, "SCPSL_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            byte[] originalMetadata = await File.ReadAllBytesAsync(metadataPath).ConfigureAwait(false);
            if (originalMetadata.Length < 8)
                throw new InvalidDataException("global-metadata.dat must contain at least 8 bytes for its signature and metadata version.");
            string originalMetadataSha256 = PatchTargetResolver.ComputeSha256(originalMetadata);
            VersionRangeInfo[] candidates = automatic ? GetUnityCandidates(info, unityVersion) : Array.Empty<VersionRangeInfo>();
            bootstrap = hashTarget ?? bootstrap ?? ResolveBootstrap(candidates, BitConverter.ToInt32(originalMetadata, 4));
            if (bootstrap == null)
                throw new InvalidDataException("No initial parser configuration matches the Unity player and metadata; select an initial patch profile.");
            if (hashTarget == null && bootstrap.gameAssemblySha256 != null)
                throw new InvalidDataException("The selected SHA-256 target does not match GameAssembly.dll.");
            byte[] normalizedMetadata = (byte[])originalMetadata.Clone();

            string parserMetadataPath = interactive ? metadataPath : Path.Combine(scratchDirectory, "global-metadata.dat");
            Directory.CreateDirectory(scratchDirectory);
            try
            {
                await LoadManagerAsync(manager, normalizedMetadata, bootstrap.metadataVersion,
                    parserMetadataPath, gamePath, createDummyDlls).ConfigureAwait(false);
                GameVersion? detectedVersion;
                VersionRangeInfo target;
                if (automatic)
                {
                    (target, detectedVersion) = ResolveDetectedTarget(info, originalSha256,
                        candidates.Where(candidate => candidate.metadataVersion == bootstrap.metadataVersion),
                        candidate => ReadGameVersion(assemblyPath, assemblyData, manager, candidate, interactive, verifyNativeReader: true));
                }
                else
                {
                    detectedVersion = ReadGameVersion(assemblyPath, assemblyData, manager, bootstrap, interactive);
                    target = PatchTargetResolver.Resolve(info, originalSha256, GetNumericVersion(detectedVersion));
                }
                if (!ReferenceEquals(target, bootstrap))
                {
                    // Use the selected target's parser and version reader together.
                    if (target.metadataVersion != bootstrap.metadataVersion)
                    {
                        await LoadManagerAsync(manager, normalizedMetadata, target.metadataVersion,
                            parserMetadataPath, gamePath, createDummyDlls).ConfigureAwait(false);
                    }
                    GameVersion? confirmedVersion = ReadGameVersion(assemblyPath, assemblyData, manager, target, interactive, verifyNativeReader: automatic);
                    if (detectedVersion.HasValue && confirmedVersion.HasValue && !detectedVersion.Value.Equals(confirmedVersion.Value))
                        throw new InvalidDataException("The selected patch target's version reader disagrees with the detected game version.");
                    detectedVersion = confirmedVersion ?? detectedVersion;
                }
                if (!ReferenceEquals(PatchTargetResolver.Resolve(info, originalSha256, GetNumericVersion(detectedVersion)), target))
                    throw new InvalidDataException("The patch target changed after reloading its configuration.");

                return new PatchSession(assemblyData, manager, target, detectedVersion, metadataPath,
                    originalMetadataSha256, normalizedMetadata);
            }
            finally
            {
                if (Directory.Exists(scratchDirectory))
                    Directory.Delete(scratchDirectory, true);
            }
        }

        private static async Task LoadManagerAsync(Il2cppManager manager, byte[] normalizedMetadata,
            int metadataVersion, string parserMetadataPath, string gamePath, bool createDummyDlls)
        {
            BitConverter.GetBytes(metadataVersion).CopyTo(normalizedMetadata, 4);
            await File.WriteAllBytesAsync(parserMetadataPath, normalizedMetadata).ConfigureAwait(false);
            await manager.LoadIl2cppAsync(parserMetadataPath, metadataData: normalizedMetadata).ConfigureAwait(false);
            await manager.DumpIl2cppAsync().ConfigureAwait(false);
            if (createDummyDlls)
                await manager.GenerateDummyDllsAsync(gamePath).ConfigureAwait(false);
            await manager.GetScriptJsonAsync().ConfigureAwait(false);
            await manager.GetMethodsDictionaryAsync().ConfigureAwait(false);
            manager.RemoveDumpFolder();
        }

        private static GameVersion? ReadGameVersion(string assemblyPath, byte[] assemblyData, Il2cppManager manager,
            VersionRangeInfo target, bool interactive, bool verifyNativeReader = false)
        {
            if (target.methods.gameVersionMethod == null)
                return null;
            Patcher reader = new Patcher(assemblyPath, assemblyData, manager, target, interactive);
            if (target.methods.gameVersionMethod.autoFindOffsets)
                reader.AutoFindGameVersionOffsets();
            if (verifyNativeReader)
                reader.VerifyGameVersionReader();
            return reader.GetGameVersion();
        }

        private static Version? GetNumericVersion(GameVersion? version)
        {
            if (!version.HasValue)
                return null;
            return new Version(version.Value.major, version.Value.minor, version.Value.revision);
        }

        /// <summary>
        /// Gets the Unity version from the game player library.
        /// </summary>
        internal static string? ReadUnityVersion(string gamePath)
        {
            string playerPath = Path.Combine(gamePath, "UnityPlayer.dll");
            if (!File.Exists(playerPath))
                return null;
            return ParseUnityVersion(FileVersionInfo.GetVersionInfo(playerPath).ProductVersion);
        }

        /// <summary>
        /// Preserves either Unity's release label or its numeric Windows resource version.
        /// The fourth numeric component is a build identifier, not an f/b release number.
        /// </summary>
        internal static string? ParseUnityVersion(string? productVersion)
        {
            string version = Regex.Match(productVersion ?? string.Empty,
                @"^\d+\.\d+\.\d+(?:[abfp]\d+|\.\d+)?(?=$|[^0-9A-Za-z.])").Value;
            return version.Length == 0 ? null : version;
        }

        private static bool MatchesUnityVersion(string launcherPath, string unityVersion)
        {
            bool numeric = Regex.IsMatch(unityVersion, @"^\d+\.\d+\.\d+(?:\.\d+)?$");
            string numericPrefix = string.Join(".", unityVersion.Split('.').Take(3));
            return launcherPath.Replace('\\', '/').Split('/').Any(component =>
                numeric ? ParseUnityVersion(component) is string version &&
                    Regex.Match(version, @"^\d+\.\d+\.\d+").Value == numericPrefix
                    : component == unityVersion);
        }

        internal static VersionRangeInfo[] GetUnityCandidates(PatchInfo info, string? unityVersion)
        {
            // Unity identifies parser candidates, never the final game target or launcher.
            return info.versionRanges.Where(target => target.gameAssemblySha256 == null &&
                (string.IsNullOrEmpty(unityVersion) || MatchesUnityVersion(target.cleanLauncherPath, unityVersion))).ToArray();
        }

        /// <summary>
        /// Chooses shared parser settings independently of native version-reader offsets.
        /// </summary>
        internal static VersionRangeInfo? ResolveUnityBootstrap(PatchInfo info, string? unityVersion, int? metadataVersion = null)
        {
            return ResolveBootstrap(GetUnityCandidates(info, unityVersion), metadataVersion);
        }

        private static VersionRangeInfo? ResolveBootstrap(VersionRangeInfo[] candidates, int? metadataVersion)
        {
            if (candidates.Length == 0)
                return null;
            int schema = candidates[0].metadataVersion;
            if (candidates.Any(target => target.metadataVersion != schema))
                return candidates.FirstOrDefault(target => target.metadataVersion == metadataVersion);
            return candidates[0];
        }

        private static (string Name, bool Auto, int Major, int Minor, int Revision, int Type)? ReaderIdentity(VersionRangeInfo target)
        {
            GameVersionMethodInfo? reader = target.methods.gameVersionMethod;
            if (reader == null)
                return null;
            // Auto-discovered offsets are mutable results, not reader configuration.
            return (reader.name, reader.autoFindOffsets,
                reader.autoFindOffsets ? 0 : reader.majorOffset,
                reader.autoFindOffsets ? 0 : reader.minorOffset,
                reader.autoFindOffsets ? 0 : reader.revisionOffset,
                reader.autoFindOffsets ? 0 : reader.typeOffset);
        }

        /// <summary>
        /// Accepts only a native identity read by the configuration of its resolved target.
        /// Multiple self-consistent identities are a real ambiguity, not a profile-order tie.
        /// </summary>
        internal static (VersionRangeInfo Target, GameVersion? Version) ResolveDetectedTarget(PatchInfo info, string originalSha256,
            IEnumerable<VersionRangeInfo> parserCandidates, Func<VersionRangeInfo, GameVersion?> readVersion)
        {
            VersionRangeInfo? hashTarget = PatchTargetResolver.ResolveHash(info, originalSha256);
            if (hashTarget != null)
                return (hashTarget, readVersion(hashTarget));
            VersionRangeInfo[] candidates = parserCandidates.ToArray();
            VersionRangeInfo? selected = null;
            GameVersion? detected = null;
            foreach (var readers in candidates.GroupBy(ReaderIdentity))
            {
                if (readers.Key == null)
                    continue;
                GameVersion? version;
                try
                {
                    version = readVersion(readers.First());
                }
                catch (Exception error) when (error is GameVersionNotFoundException or IndexOutOfRangeException or InvalidDataException)
                {
                    // A reader for another native layout is not evidence of this game version.
                    continue;
                }
                if (!version.HasValue)
                    continue;
                VersionRangeInfo? target = PatchTargetResolver.ResolveOrDefault(info, originalSha256, GetNumericVersion(version));
                if (target == null || !candidates.Contains(target) || ReaderIdentity(target) != readers.Key)
                    continue;
                if (selected != null)
                    throw new InvalidDataException("Native version readers identify multiple patch targets; select an initial patch profile.");
                selected = target;
                detected = version;
            }
            if (selected == null)
                throw new InvalidDataException("No native version reader identifies a matching patch target.");
            return (selected, detected);
        }
    }
}
