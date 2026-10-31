using Newtonsoft.Json;

namespace SCPSL_ModPatch
{
    public class Configuration
    {
        public string GameFolder_Path = string.Empty;
        public string Unlicense_Path = @".\unlicense.exe";
        public bool AutoUpdatePatchInfo = false;
        public bool CreateManagedDummyDllFolder = false;
        public bool CustomPatchInfoEnable = false;
        public string CustomPatchInfoPath = string.Empty;

        public static Configuration GetConfiguration(string filePath)
        {
            string jsonConfig = File.ReadAllText(filePath);
            return JsonConvert.DeserializeObject<Configuration>(jsonConfig) ?? new Configuration();
        }

        public void SaveConfiguration(string filePath)
        {
            string jsonConfig = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(filePath, jsonConfig);
        }
    }
}
