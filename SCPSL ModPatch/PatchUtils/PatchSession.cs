using Newtonsoft.Json;
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
            string assemblyPath = Path.Combine(gamePath, "GameAssembly.dll");
            byte[] assemblyData = await Patcher.GetGameAssemblyDataAsync(assemblyPath).ConfigureAwait(false);
            string originalSha256 = PatchTargetResolver.ComputeSha256(assemblyData);
            VersionRangeInfo? hashTarget = PatchTargetResolver.ResolveHash(info, originalSha256);
            bootstrap = hashTarget ?? bootstrap ?? ResolveUnityBootstrap(info, ReadUnityVersion(gamePath));
            if (bootstrap == null)
                throw new InvalidDataException("Select an initial patch profile when no GameAssembly.dll SHA-256 target matches.");
            if (hashTarget == null && bootstrap.gameAssemblySha256 != null)
                throw new InvalidDataException("The selected SHA-256 target does not match GameAssembly.dll.");

            string metadataPath = Path.Combine(gamePath, "SCPSL_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            byte[] originalMetadata = await File.ReadAllBytesAsync(metadataPath).ConfigureAwait(false);
            if (originalMetadata.Length < 8)
                throw new InvalidDataException("global-metadata.dat must contain at least 8 bytes for its signature and metadata version.");
            string originalMetadataSha256 = PatchTargetResolver.ComputeSha256(originalMetadata);
            byte[] normalizedMetadata = (byte[])originalMetadata.Clone();

            string scratchDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SCPSL-ModPatch", Guid.NewGuid().ToString("N")));
            string dumpDirectory = Path.Combine(scratchDirectory, "dump");
            string parserMetadataPath = interactive ? metadataPath : Path.Combine(scratchDirectory, "global-metadata.dat");
            Directory.CreateDirectory(scratchDirectory);
            try
            {
                Il2cppManager manager = await LoadManagerAsync(assemblyData, normalizedMetadata, bootstrap.metadataVersion,
                    parserMetadataPath, dumpDirectory, gamePath, interactive, createDummyDlls).ConfigureAwait(false);
                GameVersion? detectedVersion = ReadGameVersion(assemblyPath, assemblyData, manager, bootstrap, interactive);
                VersionRangeInfo target = PatchTargetResolver.Resolve(info, originalSha256, GetNumericVersion(detectedVersion));
                if (!ReferenceEquals(target, bootstrap))
                {
                    // Use the selected target's parser and version reader together.
                    if (target.metadataVersion != bootstrap.metadataVersion)
                    {
                        manager = await LoadManagerAsync(assemblyData, normalizedMetadata, target.metadataVersion,
                            parserMetadataPath, dumpDirectory, gamePath, interactive, createDummyDlls).ConfigureAwait(false);
                    }
                    GameVersion? confirmedVersion = ReadGameVersion(assemblyPath, assemblyData, manager, target, interactive);
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

        private static async Task<Il2cppManager> LoadManagerAsync(byte[] assemblyData, byte[] normalizedMetadata,
            int metadataVersion, string parserMetadataPath, string dumpDirectory, string gamePath,
            bool interactive, bool createDummyDlls)
        {
            BitConverter.GetBytes(metadataVersion).CopyTo(normalizedMetadata, 4);
            await File.WriteAllBytesAsync(parserMetadataPath, normalizedMetadata).ConfigureAwait(false);
            Il2cppManager manager = new Il2cppManager(assemblyData, dumpDirectory, interactive);
            await manager.LoadIl2cppAsync(parserMetadataPath).ConfigureAwait(false);
            await manager.DumpIl2cppAsync().ConfigureAwait(false);
            if (createDummyDlls)
                await manager.GenerateDummyDllsAsync(gamePath).ConfigureAwait(false);
            await manager.GetScriptJsonAsync().ConfigureAwait(false);
            await manager.GetMethodsDictionaryAsync().ConfigureAwait(false);
            manager.RemoveDumpFolder();
            return manager;
        }

        private static GameVersion? ReadGameVersion(string assemblyPath, byte[] assemblyData, Il2cppManager manager,
            VersionRangeInfo target, bool interactive)
        {
            if (target.methods.gameVersionMethod == null)
                return null;
            Patcher reader = new Patcher(assemblyPath, assemblyData, manager, target, interactive);
            if (target.methods.gameVersionMethod.autoFindOffsets)
                reader.AutoFindGameVersionOffsets();
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
            string productVersion = FileVersionInfo.GetVersionInfo(playerPath).ProductVersion ?? string.Empty;
            string version = Regex.Match(productVersion, @"^\d+\.\d+\.\d+[abfp]\d+").Value;
            return version.Length == 0 ? null : version;
        }

        /// <summary>
        /// Selects initial parser settings from the Unity version.
        /// </summary>
        internal static VersionRangeInfo? ResolveUnityBootstrap(PatchInfo info, string? unityVersion)
        {
            if (string.IsNullOrEmpty(unityVersion))
                return null;
            // The DLL hash and game version select the final target after parsing.
            VersionRangeInfo[] candidates = info.versionRanges.Where(target => target.gameAssemblySha256 == null &&
                target.cleanLauncherPath.Replace('\\', '/').Split('/').Contains(unityVersion, StringComparer.Ordinal)).ToArray();
            if (candidates.Length == 0)
                return null;
            string[] identities = candidates.Select(target => JsonConvert.SerializeObject(new
            {
                target.metadataVersion,
                target.methods.gameVersionMethod
            })).Distinct(StringComparer.Ordinal).ToArray();
            if (identities.Length != 1)
                throw new InvalidDataException($"Unity {unityVersion} has multiple parser configurations; supply --profile explicitly.");
            return candidates[0];
        }
    }
}
