namespace SCPSL_ModPatch.CommandLine
{
    internal sealed record FileChange(string Path, string? BeforeSha256, string? AfterSha256);
}
