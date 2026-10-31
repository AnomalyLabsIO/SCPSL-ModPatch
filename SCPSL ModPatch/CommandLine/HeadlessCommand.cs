using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SCPSL_ModPatch.IL2Cpp;
using SCPSL_ModPatch.PatchUtils;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

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

        Open this executable without arguments for the GUI, or supply a command
          for CLI mode. Patch mode never prompts, downloads, or unpacks.
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
        Exit codes: 0 success/help, 1 operation failed, 2 invalid command arguments,
          3 JSON result could not be written.
        ";

        static readonly JsonSerializerSettings _manifestJsonSettings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Error,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 64
        };

        static readonly JsonSerializerSettings _resultJsonSettings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.Indented,
            TypeNameHandling = TypeNameHandling.None
        };

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
                    : ex.Message;
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

            PatchInfo info = DeserializeManifest<PatchInfo>(await File.ReadAllTextAsync(options.PatchInfoPath));
            PatchTargetResolver.Validate(info);
            VersionRangeInfo? bootstrap = null;
            if (options.Profile != null)
            {
                VersionRangeInfo[] matches = info.versionRanges.Where(target => target.ToString() == options.Profile).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException($"Profile '{options.Profile}' must identify exactly one patch-info entry (found {matches.Length}).");
                bootstrap = matches[0];
            }

            result.Backend = "il2cpp";
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
            string[] nativeFiles = { assemblyPath, metadataPath, launcherDestination, bundleVersionPath, options.PatchInfoPath, string.Empty };
            EnsureDistinctNativeFiles(nativeFiles.AsSpan(0, 5));
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
            nativeFiles[5] = launcherPath;
            EnsureDistinctNativeFiles(nativeFiles);
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
            EnsureDistinctNativeFiles(nativeFiles);
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

        private static T DeserializeManifest<T>(string json) where T : class
        {
            return JsonConvert.DeserializeObject<T>(json, _manifestJsonSettings)
                ?? throw new InvalidDataException("Patch info is null.");
        }

        private static IEnumerable<JToken> ManifestValues(JObject value, string name)
        {
            // Json.NET binds model members case-insensitively. Inspect every spelling
            // so duplicate-case properties cannot hide an input on an error path.
            foreach (JProperty property in value.Properties())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    yield return property.Value;
        }

        private static IEnumerable<string> ManifestStrings(JObject value, string name)
        {
            foreach (JToken token in ManifestValues(value, name))
                if (token.Type is not (JTokenType.Null or JTokenType.Undefined))
                    yield return token.Value<string>()!;
        }

        private static void EnsureSafeReportPath(string[] args, string resultPath)
        {
            string bundledManifest = Path.Combine(AppContext.BaseDirectory, CliOptions.PATCHINFO_FILENAME);
            HashSet<string> manifests = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> gameDirectories = new(StringComparer.OrdinalIgnoreCase);
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
                    string gameDirectory = CanonicalPath(value);
                    if (!gameDirectories.Add(gameDirectory))
                        continue;
                    foreach (string relative in new string[] { "GameAssembly.dll", "SCPSL.exe",
                        UnityBundleVersionCorrection.RelativePath,
                        Path.Combine("SCPSL_Data", "il2cpp_data", "Metadata", "global-metadata.dat") })
                        EnsureDifferentPath(resultPath, Path.Combine(gameDirectory, relative));
                }
            }
            if (manifests.Count == 0)
                manifests.Add(Path.GetFullPath(bundledManifest));

            // Discovery shares the production parser, but retains raw members so
            // unknown schema fields cannot prevent protection of recognized inputs.
            foreach (string manifest in manifests)
            {
                try
                {
                    string json;
                    try
                    {
                        json = File.ReadAllText(manifest);
                    }
                    catch (FileNotFoundException)
                    {
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        continue;
                    }
                    JObject document = DeserializeManifest<JObject>(json);
                    foreach (JArray ranges in ManifestValues(document, nameof(PatchInfo.versionRanges)).OfType<JArray>())
                    foreach (JObject target in ranges.OfType<JObject>())
                    {
                        foreach (string launcher in ManifestStrings(target, nameof(VersionRangeInfo.cleanLauncherPath)))
                            if (!string.IsNullOrWhiteSpace(launcher))
                                EnsureDifferentPath(resultPath, Path.GetFullPath(launcher, Path.GetDirectoryName(manifest)!));
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    throw new UnsafeReportPathException($"Cannot verify --result safety against patch definitions '{manifest}': {ex.Message} No report was written.");
                }
            }
        }

        private static void EnsureDistinctNativeFiles(ReadOnlySpan<string> paths)
        {
            Dictionary<string, string> resolvedPaths = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<NativeFileIdentity, string> fileIdentities = new();
            foreach (string path in paths)
            {
                string resolved = CanonicalPath(path);
                if (resolvedPaths.TryGetValue(resolved, out string? alias))
                    throw new InvalidDataException($"Native game paths must refer to distinct files: '{alias}' and '{path}'.");
                resolvedPaths.Add(resolved, path);
                NativeFileIdentity? identity = NativeFileIdentity.ReadIfPresent(resolved);
                if (!identity.HasValue)
                    continue;
                if (fileIdentities.TryGetValue(identity.Value, out alias))
                    throw new InvalidDataException($"Native game paths must refer to distinct files: '{alias}' and '{path}'.");
                fileIdentities.Add(identity.Value, path);
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
            using FileStream file = File.OpenRead(path);
            using SHA256 hash = SHA256.Create();
            return Convert.ToHexString(hash.ComputeHash(file));
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
                File.WriteAllText(temporary, JsonConvert.SerializeObject(result, _resultJsonSettings) + Environment.NewLine);
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
