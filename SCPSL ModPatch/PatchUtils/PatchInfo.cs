using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SCPSL_ModPatch.PatchUtils
{
    public class PatchInfo
    {
        public VersionRangeInfo[] versionRanges = Array.Empty<VersionRangeInfo>();
    }

    public class VersionRangeInfo
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? backend = null;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? assemblyPath = null;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? assemblySha256 = null;
        public string versionRange = string.Empty;
        public string versionFrom = string.Empty;
        public string versionTo = string.Empty;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? version = null;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? gameAssemblySha256 = null;

        public string cleanLauncherPath = string.Empty;
        public string? cleanLauncherUrl = null;
        public int metadataVersion;
        public MethodsInfo methods = new();

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(versionRange))
                return versionRange;
            if (gameAssemblySha256 != null)
                return $"SHA-256: {gameAssemblySha256}";
            if (assemblySha256 != null)
                return $"SHA-256: {assemblySha256}";
            if (version != null)
                return version;
            if (string.IsNullOrEmpty(versionFrom))
                return "Undefined version range";
            if (string.IsNullOrEmpty(versionTo))
                return versionFrom;
            return $"{versionFrom} – {versionTo}";
        }
    }

    public class MethodsInfo
    {
        public PatchMethodInfo[] patchMethods = Array.Empty<PatchMethodInfo>();
        public GameVersionMethodInfo? gameVersionMethod = null;
    }

    public class PatchMethodInfo
    {
        public string name = string.Empty;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public MonoInstructionInfo[]? ilPattern = null;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public MonoEditInfo? ilEdit = null;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? patchOffset = null; // Offset from the method start. Null means zero for offset patches.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? patchPattern = null; // Unique byte pattern inside the method.
        public int patternOffset = 0; // Offset from the pattern match. Requires patchPattern.
        public string patchData = string.Empty; // Hex-coded patch data.
        public int? patchSize = null; // Size of patching area.
        public bool nowarning = false; // Don't show a warning if the method doesn't exist.
        public string? patchifnotexists = null; // Patches the method if specified method doesn't exist.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? patchifexists = null; // Mono: patches only when this full method signature exists.
    }

    public class GameVersionMethodInfo
    {
        public string name = "GameCore.Version$$.cctor";
        public bool autoFindOffsets = false;
        public int majorOffset;
        public int minorOffset;
        public int revisionOffset;
        public int typeOffset;
    }
}
