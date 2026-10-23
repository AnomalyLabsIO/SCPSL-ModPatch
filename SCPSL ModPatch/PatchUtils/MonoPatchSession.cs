namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>Loads, plans and saves a managed assembly without executing game code.</summary>
    internal sealed class MonoPatchSession
    {
        internal const string DEFAULT_ASSEMBLY_PATH = "SCPSL_Data/Managed/Assembly-CSharp.dll";
        internal const string ALTERNATE_ASSEMBLY_PATH = "SCPSL_Mono/Managed/Assembly-CSharp.dll";
        public string AssemblyPath { get; }
        public byte[] AssemblyData { get; }
        public string OriginalSha256 { get; }
        public VersionRangeInfo Target { get; }
        public string? VersionLabel { get; }
        public string BackupPath => GetBackupPath(AssemblyPath, OriginalSha256);
        public IReadOnlyList<PatchOutcome> PatchOutcomes { get; private set; } = Array.Empty<PatchOutcome>();
        readonly string _gamePath;
        string? _preparedSha256;

        private MonoPatchSession(string gamePath, string path, byte[] data, VersionRangeInfo target, string? versionLabel)
        {
            _gamePath = Path.GetFullPath(gamePath);
            AssemblyPath = path;
            AssemblyData = data;
            OriginalSha256 = PatchTargetResolver.ComputeSha256(data);
            Target = target;
            VersionLabel = versionLabel;
        }

        public static bool IsMonoGame(string gamePath, PatchInfo info)
        {
            return !File.Exists(Path.Combine(gamePath, "GameAssembly.dll")) &&
                PatchTargetResolver.ForBackend(info, "mono").versionRanges.Any(target =>
                    File.Exists(ResolveAssemblyPath(gamePath, target)));
        }

        internal static string ResolveAssemblyPath(string gamePath, VersionRangeInfo target)
        {
            string root = Path.GetFullPath(gamePath);
            string relative = target.assemblyPath ?? DEFAULT_ASSEMBLY_PATH;
            if (target.assemblyPath == null)
            {
                string[] existing = new[] { DEFAULT_ASSEMBLY_PATH, ALTERNATE_ASSEMBLY_PATH }
                    .Where(candidate => File.Exists(Path.Combine(root, candidate.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
                if (existing.Length > 1)
                    throw new InvalidDataException("Both standard Mono assembly locations exist; specify assemblyPath in the target profile.");
                if (existing.Length == 1) relative = existing[0];
            }
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
                throw new InvalidDataException("Mono assemblyPath must be relative to the game directory.");
            string path = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), root);
            string within = Path.GetRelativePath(root, path);
            if (within == ".." || within.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(within) || within == ".")
                throw new InvalidDataException("Mono assemblyPath must stay inside the game directory.");
            string current = root;
            foreach (string part in within.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Mono assemblyPath cannot traverse a symbolic link or junction.");
            }
            return path;
        }

        public static async Task<MonoPatchSession> LoadAsync(string gamePath, PatchInfo info, VersionRangeInfo? bootstrap = null)
        {
            PatchInfo managed = PatchTargetResolver.ForBackend(info, "mono");
            if (bootstrap != null && PatchTargetResolver.GetBackend(bootstrap) != "mono")
                throw new InvalidDataException("The selected profile is not a Mono profile.");
            string[] paths = (bootstrap == null ? managed.versionRanges : new[] { bootstrap })
                .Select(target => ResolveAssemblyPath(gamePath, target)).Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length != 1)
                throw new InvalidDataException("Expected one Mono target assembly; select --profile when multiple assemblies are present.");
            string path = paths[0];
            byte[] data = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            string hash = PatchTargetResolver.ComputeSha256(data);
            string? label = MonoVersionReader.Read(data, path);
            Version? version = MonoVersionReader.ParseVersion(label);
            var sameAssembly = new PatchInfo { versionRanges = managed.versionRanges.Where(target =>
                string.Equals(ResolveAssemblyPath(gamePath, target), path, StringComparison.OrdinalIgnoreCase)).ToArray() };
            VersionRangeInfo selected = PatchTargetResolver.Resolve(sameAssembly, hash, version, "mono", label);
            return new MonoPatchSession(gamePath, path, data, selected, label);
        }

        public byte[] Prepare()
        {
            _preparedSha256 = null;
            if (PatchTargetResolver.ComputeSha256(AssemblyData) != OriginalSha256)
                throw new InvalidDataException("Mono assembly data changed after loading.");
            var patcher = new MonoPatcher(AssemblyData, Target, Path.GetDirectoryName(AssemblyPath));
            byte[] rewritten = patcher.Rewrite();
            _preparedSha256 = PatchTargetResolver.ComputeSha256(rewritten);
            PatchOutcomes = patcher.PatchOutcomes.ToArray();
            return rewritten;
        }

        internal static string GetBackupPath(string path, string hash) => path + ".modpatch." + hash + ".bak";

        /// <summary>Saves a validated result atomically and retains a verified original backup.</summary>
        public void Save(byte[] rewritten)
        {
            if (_preparedSha256 == null || PatchTargetResolver.ComputeSha256(rewritten) != _preparedSha256)
                throw new InvalidDataException("Prepare and validate the Mono rewrite before saving.");
            if (PatchTargetResolver.ComputeSha256(AssemblyData) != OriginalSha256)
                throw new InvalidDataException("The original Mono assembly data changed after preparation.");
            VerifyOriginal();
            if (_preparedSha256 == OriginalSha256) return;
            string temporary = AssemblyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backupTemporary = temporary + ".bak";
            try
            {
                File.WriteAllBytes(temporary, rewritten);
                if (PatchTargetResolver.ComputeSha256(File.ReadAllBytes(temporary)) != _preparedSha256)
                    throw new IOException("The staged Mono rewrite failed verification.");
                VerifyOriginal();
                if (!File.Exists(BackupPath))
                {
                    File.WriteAllBytes(backupTemporary, AssemblyData);
                    if (PatchTargetResolver.ComputeSha256(File.ReadAllBytes(backupTemporary)) != OriginalSha256)
                        throw new IOException("The staged Mono backup failed verification.");
                    File.Move(backupTemporary, BackupPath, overwrite: false);
                }
                if ((File.GetAttributes(BackupPath) & FileAttributes.ReparsePoint) != 0 ||
                    PatchTargetResolver.ComputeSha256(File.ReadAllBytes(BackupPath)) != OriginalSha256)
                    throw new IOException("The existing Mono backup does not match the original assembly.");
                VerifyOriginal();
                File.Replace(temporary, AssemblyPath, destinationBackupFileName: null);
                if (PatchTargetResolver.ComputeSha256(File.ReadAllBytes(AssemblyPath)) != _preparedSha256)
                    throw new IOException("The saved Mono assembly failed verification; the original backup is retained.");
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(backupTemporary)) File.Delete(backupTemporary);
            }
        }

        private void VerifyOriginal()
        {
            if (!string.Equals(ResolveAssemblyPath(_gamePath, Target), AssemblyPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The Mono assembly path changed after loading.");
            if ((File.GetAttributes(AssemblyPath) & FileAttributes.ReparsePoint) != 0 ||
                PatchTargetResolver.ComputeSha256(File.ReadAllBytes(AssemblyPath)) != OriginalSha256)
                throw new IOException("The Mono assembly changed on disk after loading; reload it before patching.");
        }
    }
}
