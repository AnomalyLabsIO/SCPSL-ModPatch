namespace SCPSL_ModPatch.CommandLine
{
    internal sealed class ReportWriteException : Exception
    {
        public ReportWriteException(Exception inner) : base($"JSON result cannot be written: {inner.Message}", inner)
        {
        }
    }
}
