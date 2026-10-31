namespace SCPSL_ModPatch
{
    [Serializable]
    public class FasmAssembleException : Exception
    {
        public FasmAssembleException(string message) : base(message) { }
    }
}
