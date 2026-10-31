using System;

namespace SCPSL_ModPatch.IL2Cpp
{
    [Serializable]
    public class Il2cppInitException : Exception
    {
        public Il2cppInitException(string message) : base(message) { }
    }
}
