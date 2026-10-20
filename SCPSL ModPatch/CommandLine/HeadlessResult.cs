using SCPSL_ModPatch.PatchUtils;

namespace SCPSL_ModPatch.CommandLine
{
    internal sealed class HeadlessResult
    {
        public int SchemaVersion { get; } = 1;
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string Command { get; set; } = string.Empty;
        public string Stage { get; set; } = "args";
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset CompletedUtc { get; set; }
        public string? GameVersion { get; set; }
        public string? Profile { get; set; }
        public int ProfileIndex { get; set; } = -1;
        public string? UnityVersion { get; set; }
        public int MetadataVersion { get; set; }
        public string? MetadataMode { get; set; }
        public bool MetadataNormalizedForParser { get; set; }
        public CommandError? Error { get; set; }
        public string? Help { get; set; }
        public List<PatchOutcome> Patches { get; } = new List<PatchOutcome>();
        public List<FileChange> ModifiedFiles { get; } = new List<FileChange>();
        public List<string> Warnings { get; } = new List<string>();
    }
}
