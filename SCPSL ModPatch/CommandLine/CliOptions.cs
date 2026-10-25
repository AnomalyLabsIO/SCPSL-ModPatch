namespace SCPSL_ModPatch.CommandLine
{
    internal sealed class CliOptions
    {
        internal const string PATCHINFO_FILENAME = "patchinfo.json";
        public bool Help { get; private init; }
        public string GameDirectory { get; private init; } = string.Empty;
        public string PatchInfoPath { get; private init; } = string.Empty;
        public string? Profile { get; private init; }
        public string? CleanLauncherPath { get; private init; }
        public string? ResultPath { get; private init; }
        public string MetadataMode { get; private init; } = "write";
        public string BundleVersionMode { get; private init; } = "preserve";

        /// <summary>
        /// Gets command options from the supplied arguments.
        /// </summary>
        public static CliOptions Parse(string[] args)
        {
            if (args.Length == 2 && args[0] == "patch" && args[1] is "--help" or "-h")
                return new CliOptions { Help = true };
            bool help = args.Length > 0 && args[0] is "--help" or "-h" or "help";
            if (args.Length == 0 || (!help && args[0] != "patch"))
                throw new CliUsageException("Expected 'patch' or '--help'.");

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> knownOptions = new HashSet<string>(StringComparer.Ordinal)
            {
                "--game-dir", "--patch-info", "--profile", "--clean-launcher", "--result", "--metadata-mode", "--bundle-version-mode"
            };
            for (int i = 1; i < args.Length; i++)
            {
                string option = args[i];
                if (!knownOptions.Contains(option) || (help && option != "--result"))
                    throw new CliUsageException($"Unknown option '{option}'.");
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new CliUsageException($"Option {option} requires a value.");
                if (!values.TryAdd(option, args[i]))
                    throw new CliUsageException($"Option {option} was supplied more than once.");
            }

            if (!help)
            {
                foreach (string required in new string[] { "--game-dir", "--result" })
                {
                    if (!values.ContainsKey(required))
                        throw new CliUsageException($"Required option {required} is missing.");
                }
            }

            string metadataMode = values.GetValueOrDefault("--metadata-mode") ?? "write";
            if (metadataMode is not "write" and not "preserve")
                throw new CliUsageException("--metadata-mode must be 'write' or 'preserve'.");
            string bundleVersionMode = values.GetValueOrDefault("--bundle-version-mode") ?? "preserve";
            if (bundleVersionMode is not "correct" and not "preserve")
                throw new CliUsageException("--bundle-version-mode must be 'correct' or 'preserve'.");
            return new CliOptions
            {
                Help = help,
                MetadataMode = metadataMode,
                BundleVersionMode = bundleVersionMode,
                GameDirectory = values.TryGetValue("--game-dir", out string? game) ? Path.GetFullPath(game) : string.Empty,
                PatchInfoPath = Path.GetFullPath(values.GetValueOrDefault("--patch-info") ?? Path.Combine(AppContext.BaseDirectory, PATCHINFO_FILENAME)),
                Profile = values.GetValueOrDefault("--profile"),
                CleanLauncherPath = values.TryGetValue("--clean-launcher", out string? launcher) ? Path.GetFullPath(launcher) : null,
                ResultPath = values.TryGetValue("--result", out string? result) ? Path.GetFullPath(result) : null
            };
        }
    }
}
