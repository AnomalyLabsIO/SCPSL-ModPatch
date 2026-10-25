using Newtonsoft.Json;
using SCPSL_ModPatch.IL2Cpp;
using SCPSL_ModPatch.PatchUtils;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SCPSL_ModPatch.CommandLine
{
    internal static class HeadlessCommand
    {
        internal const string USAGE_MESSAGE = @"Usage:
          ""SCPSL ModPatch.exe"" patch --game-dir PATH --result PATH
              [--patch-info PATH] [--profile LABEL] [--clean-launcher PATH]
              [--metadata-mode write|preserve]
              [--bundle-version-mode preserve|correct]
          ""SCPSL ModPatch.exe"" --help [--result PATH]

        The GUI build opens its window with no arguments; the console build requires
          a command. Patch mode never prompts, downloads, or unpacks.
        --patch-info defaults to patchinfo.json beside the executable.
        --profile selects the initial parser profile by its exact display label;
          omit it when a DLL hash matches, the manifest has one profile, or
          UnityPlayer.dll identifies an unambiguous parser configuration.
        The detected DLL hash/version still determines the final patch target.
        --clean-launcher overrides the selected profile's local launcher path.
        --metadata-mode preserve keeps runtime metadata byte-identical; only a
          private parser copy is normalized. The default 'write' saves its header.
        --bundle-version-mode correct synchronizes globalgamemanagers' displayed
          version with the detected numeric IL2CPP game version (10 or later).
          The default 'preserve' leaves this Unity settings file unchanged.
        Manifest launcher paths are relative to the manifest; explicit paths are
          relative to the caller's working directory. The result parent must exist.
        Mono games use the selected managed assembly and a verified original backup;
          no launcher or IL2CPP metadata is changed. The backend is inferred from
          IL edits when omitted; older native manifests continue to use IL2CPP.
        Exit codes: 0 success/help, 1 operation failed, 2 invalid command arguments,
          3 JSON result could not be written.
        ";

        static readonly JsonSerializerOptions _resultJsonOptions = new JsonSerializerOptions()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        /// <summary>
        /// Runs a command and writes its JSON result.
        /// </summary>
        public static int Run(string[] args)
        {
            AttachParentConsole();
            HeadlessResult result = new HeadlessResult { Command = args.FirstOrDefault() ?? string.Empty };
            Dictionary<string, string?> originalHashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            string? requestedResultPath = FindResultPath(args);
            string? resultPath = null;
            try
            {
                // Also protect inputs when argument parsing itself fails.
                if (requestedResultPath != null)
                {
                    EnsureSafeReportPath(args, requestedResultPath);
                    resultPath = requestedResultPath;
                }
                CliOptions options = CliOptions.Parse(args);
                resultPath = options.ResultPath;
                result.Command = options.Help ? "help" : "patch";
                result.Stage = "preflight";
                result.BundleVersionMode = options.BundleVersionMode;
                if (resultPath != null)
                {
                    try
                    {
                        CheckReportWritable(resultPath);
                    }
                    catch (Exception ex)
                    {
                        throw new ReportWriteException(ex);
                    }
                }

                if (options.Help)
                {
                    Console.Out.WriteLine(USAGE_MESSAGE);
                    result.Help = USAGE_MESSAGE;
                }
                else
                {
                    ExecuteAsync(options, result, originalHashes).GetAwaiter().GetResult();
                }
                result.Stage = "complete";
                result.Success = true;
                result.ExitCode = 0;
            }
            catch (Exception ex)
            {
                result.Success = false;
                if (ex is ReportWriteException)
                    result.ExitCode = 3;
                else if (result.Stage == "args" || ex is CliUsageException)
                    result.ExitCode = 2;
                else
                    result.ExitCode = 1;
                string message = ex is FileIsProtectedException
                    ? "GameAssembly.dll is protected. Unpack it before running CLI mode; automatic unpacking is unavailable."
                    : ex.InnerException?.Message ?? ex.Message;
                result.Error = new CommandError(ex.GetType().Name, message);
                Console.Error.WriteLine($"ModPatch failed ({result.Stage}): {result.Error.Message}");
                if (result.ExitCode == 2)
                    Console.Error.WriteLine(USAGE_MESSAGE);
                // Never overwrite an input even to report the command's invalid result path.
                if (ex is UnsafeReportPathException)
                    resultPath = null;
            }

            foreach (KeyValuePair<string, string?> originalHash in originalHashes)
            {
                try
                {
                    string? finalHash = HashFileIfPresent(originalHash.Key);
                    if (!string.Equals(originalHash.Value, finalHash, StringComparison.OrdinalIgnoreCase))
                        result.ModifiedFiles.Add(new FileChange(originalHash.Key, originalHash.Value, finalHash));
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Could not inspect final file '{originalHash.Key}': {ex.Message}");
                }
            }
            result.CompletedUtc = DateTimeOffset.UtcNow;
            if (resultPath != null)
            {
                try
                {
                    WriteResult(resultPath, result);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Could not write JSON result '{resultPath}': {ex.Message}");
                    return 3;
                }
            }
            return result.ExitCode;
        }

        private static async Task ExecuteAsync(CliOptions options, HeadlessResult result, Dictionary<string, string?> originalHashes)
        {
            if (!Directory.Exists(options.GameDirectory))
                throw new DirectoryNotFoundException($"Game directory does not exist: {options.GameDirectory}");
            string assemblyPath = Path.Combine(options.GameDirectory, "GameAssembly.dll");
            string metadataPath = Path.Combine(options.GameDirectory, "SCPSL_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            string launcherDestination = Path.Combine(options.GameDirectory, "SCPSL.exe");
            string bundleVersionPath = Path.GetFullPath(Path.Combine(options.GameDirectory, UnityBundleVersionCorrection.RelativePath));
            foreach (string required in new string[] { options.PatchInfoPath })
            {
                if (!File.Exists(required))
                    throw new FileNotFoundException($"Required input does not exist: {required}", required);
            }

            PatchInfo info = JsonConvert.DeserializeObject<PatchInfo>(await File.ReadAllTextAsync(options.PatchInfoPath),
                new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Error,
                    MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                    TypeNameHandling = TypeNameHandling.None,
                    MaxDepth = 64
                })
                ?? throw new InvalidDataException("Patch info is null.");
            PatchTargetResolver.Validate(info);
            VersionRangeInfo? bootstrap = null;
            if (options.Profile != null)
            {
                VersionRangeInfo[] matches = info.versionRanges.Where(target => target.ToString() == options.Profile).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException($"Profile '{options.Profile}' must identify exactly one patch-info entry (found {matches.Length}).");
                bootstrap = matches[0];
            }
            else if (info.versionRanges.Length == 1)
                bootstrap = info.versionRanges[0];

            if (bootstrap != null && PatchTargetResolver.GetBackend(bootstrap) == "mono" ||
                MonoPatchSession.IsMonoGame(options.GameDirectory, info))
            {
                if (options.BundleVersionMode == "correct")
                    throw new InvalidDataException("Bundle-version correction is available only for numeric IL2CPP games; use --bundle-version-mode preserve for Mono.");
                await ExecuteMonoAsync(options, info, bootstrap, result, originalHashes).ConfigureAwait(false);
                return;
            }
            foreach (string required in new[] { assemblyPath, metadataPath })
                if (!File.Exists(required))
                    throw new FileNotFoundException($"Required input does not exist: {required}", required);

            string manifestDirectory = Path.GetDirectoryName(options.PatchInfoPath)!;
            foreach (VersionRangeInfo target in info.versionRanges)
            {
                if (!string.IsNullOrWhiteSpace(target.cleanLauncherPath))
                    EnsureDifferentPath(options.ResultPath!, Path.GetFullPath(target.cleanLauncherPath, manifestDirectory));
            }

            if (options.CleanLauncherPath != null && !File.Exists(options.CleanLauncherPath))
                throw new FileNotFoundException("The local clean launcher does not exist. Downloads are unavailable in CLI mode.", options.CleanLauncherPath);
            foreach (string path in new string[] { assemblyPath, metadataPath, launcherDestination })
            {
                originalHashes[path] = HashFileIfPresent(path);
            }
            if (options.BundleVersionMode == "correct")
            {
                if (!File.Exists(bundleVersionPath)) throw new FileNotFoundException("Bundle-version correction requires globalgamemanagers.", bundleVersionPath);
                originalHashes[bundleVersionPath] = HashFileIfPresent(bundleVersionPath);
            }

            result.Stage = "load";
            PatchSession session = await PatchSession.LoadAsync(options.GameDirectory, info, bootstrap, interactive: false);
            result.GameVersion = session.DetectedVersion?.ToString();
            result.Profile = session.Target.ToString();
            result.ProfileIndex = Array.IndexOf(info.versionRanges, session.Target);
            result.UnityVersion = PatchSession.ReadUnityVersion(options.GameDirectory);
            result.MetadataVersion = session.Target.metadataVersion;
            result.MetadataMode = options.MetadataMode;
            result.MetadataNormalizedForParser = !string.Equals(PatchTargetResolver.ComputeSha256(session.NormalizedMetadata),
                session.OriginalMetadataSha256, StringComparison.OrdinalIgnoreCase);
            string launcherPath;
            if (options.CleanLauncherPath != null)
                launcherPath = options.CleanLauncherPath;
            else if (!string.IsNullOrWhiteSpace(session.Target.cleanLauncherPath))
                launcherPath = Path.GetFullPath(session.Target.cleanLauncherPath, manifestDirectory);
            else
                throw new InvalidDataException("The selected profile has no local clean launcher; supply --clean-launcher.");
            foreach (string path in new string[] { assemblyPath, metadataPath, launcherDestination, bundleVersionPath, options.PatchInfoPath })
            {
                if (SamePath(launcherPath, path))
                    throw new InvalidDataException("The clean launcher must be a separate input file.");
            }
            EnsureDifferentPath(options.ResultPath!, launcherPath);
            byte[] launcher = await File.ReadAllBytesAsync(launcherPath);
            if (launcher.Length == 0)
                throw new InvalidDataException("The clean launcher is empty.");

            UnityBundleVersionCorrection? bundleVersion = null;
            if (options.BundleVersionMode == "correct")
            {
                bundleVersion = UnityBundleVersionCorrection.Prepare(bundleVersionPath,
                    result.GameVersion ?? throw new InvalidDataException("Bundle-version correction requires a detected numeric game version."));
                if (bundleVersion.OriginalSha256 != originalHashes[bundleVersionPath])
                    throw new IOException("globalgamemanagers changed while loading the game.");
            }

            result.Stage = "patch";
            Patcher patcher = new Patcher(assemblyPath, session.AssemblyData, session.Manager, session.Target, interactive: false);
            patcher.PatchGameAssembly();
            result.Patches.AddRange(patcher.PatchOutcomes.Select(patch => patch.Status == "applied"
                ? patch with { Status = "prepared", Message = "Prepared in memory; not yet saved." } : patch));

            result.Stage = "save";
            // Preflight every destination before the first game-file write.
            foreach (KeyValuePair<string, string?> originalHash in originalHashes)
            {
                string path = originalHash.Key;
                if (Directory.Exists(path))
                    throw new IOException($"A game-file destination names a directory: {path}");
                if (!string.Equals(HashFileIfPresent(path), originalHash.Value, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Input changed while preparing patches: {path}");
                if (options.MetadataMode == "preserve" && SamePath(path, metadataPath))
                    continue;
                if (File.Exists(path))
                {
                    using (FileStream writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                }
                else
                {
                    using (FileStream writable = new FileStream(path + "." + Guid.NewGuid().ToString("N") + ".tmp", FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose))
                    {
                    }
                }
            }
            if (!string.Equals(session.OriginalMetadataSha256, originalHashes[metadataPath], StringComparison.OrdinalIgnoreCase))
                throw new IOException("Metadata changed while loading IL2CPP.");
            patcher.SaveGameAssembly();
            result.Patches.Clear();
            result.Patches.AddRange(patcher.PatchOutcomes);
            if (options.MetadataMode == "write" && result.MetadataNormalizedForParser)
            {
                using (FileStream metadata = new FileStream(metadataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                using (SHA256 hash = SHA256.Create())
                {
                    if (!string.Equals(Convert.ToHexString(hash.ComputeHash(metadata)), session.OriginalMetadataSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Metadata changed before saving its header.");
                    metadata.Position = 4;
                    metadata.Write(session.NormalizedMetadata, 4, 4);
                }
            }
            await File.WriteAllBytesAsync(launcherDestination, launcher);
            if (bundleVersion != null)
            {
                bundleVersion.Save();
                result.BundleVersion = new BundleVersionChange(bundleVersion.Path, bundleVersion.Before, bundleVersion.After, bundleVersion.Status);
            }
        }

        private static async Task ExecuteMonoAsync(CliOptions options, PatchInfo info, VersionRangeInfo? bootstrap,
            HeadlessResult result, Dictionary<string, string?> originalHashes)
        {
            if (options.CleanLauncherPath != null)
                throw new InvalidDataException("Mono patching does not replace a launcher; omit --clean-launcher.");
            result.Backend = "mono";
            result.MetadataMode = "preserve";
            result.Stage = "load";
            MonoPatchSession session = await MonoPatchSession.LoadAsync(options.GameDirectory, info, bootstrap).ConfigureAwait(false);
            EnsureDifferentPath(options.ResultPath!, session.AssemblyPath);
            EnsureDifferentPath(options.ResultPath!, session.BackupPath);
            result.AssemblyPath = session.AssemblyPath;
            result.BackupPath = session.BackupPath;
            result.GameVersion = session.VersionLabel;
            result.Profile = session.Target.ToString();
            result.ProfileIndex = Array.IndexOf(info.versionRanges, session.Target);
            result.UnityVersion = PatchSession.ReadUnityVersion(options.GameDirectory);
            originalHashes[session.AssemblyPath] = session.OriginalSha256;
            originalHashes[session.BackupPath] = HashFileIfPresent(session.BackupPath);
            result.Stage = "patch";
            byte[] rewritten = session.Prepare();
            if (PatchTargetResolver.ComputeSha256(rewritten) == session.OriginalSha256)
                result.BackupPath = null;
            result.Patches.AddRange(session.PatchOutcomes.Select(patch => patch.Status == "applied"
                ? patch with { Status = "prepared", Message = "Prepared in memory; not yet saved." } : patch));
            result.Stage = "save";
            session.Save(rewritten);
            result.Patches.Clear();
            result.Patches.AddRange(session.PatchOutcomes);
        }

        private static string? FindResultPath(string[] args)
        {
            int[] matches = Enumerable.Range(0, args.Length).Where(i => args[i] == "--result").ToArray();
            if (matches.Length != 1)
                return null;
            int value = matches[0] + 1;
            if (value >= args.Length || string.IsNullOrWhiteSpace(args[value]) || args[value].StartsWith("--", StringComparison.Ordinal))
                return null;
            try
            {
                return Path.GetFullPath(args[value]);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void EnsureSafeReportPath(string[] args, string resultPath)
        {
            string bundledManifest = Path.Combine(AppContext.BaseDirectory, CliOptions.PATCHINFO_FILENAME);
            HashSet<string> manifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bundledManifest };
            List<string> gameDirectories = new List<string>();
            EnsureDifferentPath(resultPath, bundledManifest);
            for (int i = 0; i + 1 < args.Length; i++)
            {
                string value = args[i + 1];
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                    continue;
                if (args[i] is "--patch-info" or "--clean-launcher")
                    EnsureDifferentPath(resultPath, value);
                if (args[i] == "--patch-info")
                    manifests.Add(Path.GetFullPath(value));
                if (args[i] == "--game-dir")
                {
                    gameDirectories.Add(Path.GetFullPath(value));
                    foreach (string relative in new string[] { "GameAssembly.dll", "SCPSL.exe", MonoPatchSession.DEFAULT_ASSEMBLY_PATH,
                        MonoPatchSession.ALTERNATE_ASSEMBLY_PATH,
                        UnityBundleVersionCorrection.RelativePath,
                        Path.Combine("SCPSL_Data", "il2cpp_data", "Metadata", "global-metadata.dat") })
                    {
                        EnsureDifferentPath(resultPath, Path.Combine(value, relative));
                    }
                    foreach (string dataFolder in new[] { "SCPSL_Data", "SCPSL_Mono" })
                        foreach (string scene in new[] { "level0", "level1", "globalgamemanagers.assets" })
                            EnsureDifferentPath(resultPath, Path.Combine(value, dataFolder, scene));
                }
            }
            // Protect manifest-supplied launchers even when an earlier argument/input check fails.
            foreach (string manifest in manifests)
            {
                try
                {
                    if (!File.Exists(manifest))
                        continue;
                    using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest)))
                    {
                        if (document.RootElement.ValueKind != JsonValueKind.Object ||
                            !document.RootElement.TryGetProperty("versionRanges", out JsonElement ranges) || ranges.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (JsonElement target in ranges.EnumerateArray())
                        {
                            if (target.ValueKind == JsonValueKind.Object && target.TryGetProperty("cleanLauncherPath", out JsonElement launcher) &&
                                launcher.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(launcher.GetString()))
                                EnsureDifferentPath(resultPath, Path.GetFullPath(launcher.GetString()!, Path.GetDirectoryName(manifest)!));
                            if (target.ValueKind == JsonValueKind.Object)
                            {
                                string[] relatives = target.TryGetProperty("assemblyPath", out JsonElement assembly) &&
                                    assembly.ValueKind == JsonValueKind.String ? new[] { assembly.GetString()! } :
                                    new[] { MonoPatchSession.DEFAULT_ASSEMBLY_PATH, MonoPatchSession.ALTERNATE_ASSEMBLY_PATH };
                                foreach (string relative in relatives)
                                foreach (string gameDirectory in gameDirectories)
                                {
                                    string managedPath = Path.GetFullPath(relative, gameDirectory);
                                    EnsureDifferentPath(resultPath, managedPath);
                                    if (target.TryGetProperty("assemblySha256", out JsonElement hash) &&
                                        hash.ValueKind == JsonValueKind.String && hash.GetString() is string declaredHash &&
                                        declaredHash.Length == 64 && declaredHash.All(Uri.IsHexDigit))
                                        EnsureDifferentPath(resultPath, MonoPatchSession.GetBackupPath(managedPath, declaredHash));
                                    string managedDirectory = Path.GetDirectoryName(managedPath)!;
                                    string? dataDirectory = Path.GetDirectoryName(managedDirectory);
                                    if (dataDirectory != null)
                                        foreach (string scene in new[] { "level0", "level1", "globalgamemanagers.assets" })
                                            EnsureDifferentPath(resultPath, Path.Combine(dataDirectory, scene));
                                    if (Directory.Exists(managedDirectory))
                                    {
                                        string prefix = Path.GetFileName(managedPath) + ".modpatch.";
                                        foreach (string backup in Directory.EnumerateFiles(managedDirectory))
                                        {
                                            string name = Path.GetFileName(backup);
                                            string extension = Path.GetExtension(name);
                                            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                                                extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                                                EnsureDifferentPath(resultPath, backup);
                                            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                                name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) &&
                                                name.Length == prefix.Length + 64 + 4 &&
                                                name.Substring(prefix.Length, 64).All(Uri.IsHexDigit))
                                                EnsureDifferentPath(resultPath, backup);
                                        }
                                    }
                                    if (File.Exists(managedPath))
                                        EnsureDifferentPath(resultPath, MonoPatchSession.GetBackupPath(managedPath, HashFileIfPresent(managedPath)!));
                                }
                            }
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // The manifest loader reports malformed JSON.
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static void EnsureDifferentPath(string resultPath, string inputPath)
        {
            if (SamePath(resultPath, inputPath))
                throw new UnsafeReportPathException("--result must not overwrite an input or a game file.");
        }

        private static bool SamePath(string first, string second)
        {
            return string.Equals(CanonicalPath(first), CanonicalPath(second), StringComparison.OrdinalIgnoreCase);
        }

        private static string CanonicalPath(string path)
        {
            string full = Path.GetFullPath(path);
            string current = Path.GetPathRoot(full)!;
            foreach (string component in full.Substring(current.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (entry.LinkTarget != null)
                    current = entry.ResolveLinkTarget(true)!.FullName;
            }
            return Path.TrimEndingDirectorySeparator(current);
        }

        private static string? HashFileIfPresent(string path)
        {
            if (!File.Exists(path))
                return null;
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }

        private static void CheckReportWritable(string path)
        {
            if (Directory.Exists(path))
                throw new IOException("The JSON result path names a directory.");
            if (File.Exists(path))
            {
                using (FileStream existing = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                }
            }
            using (FileStream probe = new FileStream(path + "." + Guid.NewGuid().ToString("N") + ".tmp", FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose))
            {
            }
        }

        private static void WriteResult(string path, HeadlessResult result)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(result, _resultJsonOptions) + Environment.NewLine);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint processId);

        private static void AttachParentConsole()
        {
            // A WinExe has no console of its own. Never allocate a new console window.
            if (OperatingSystem.IsWindows() && AttachConsole(unchecked((uint)-1)))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }
        }
    }
}
