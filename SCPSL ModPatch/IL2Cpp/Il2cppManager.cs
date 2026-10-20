using Il2CppDumper;
using Newtonsoft.Json;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace SCPSL_ModPatch.IL2Cpp
{
    internal class Il2cppManager
    {
        const string IL2CPP_FOLDER = @".\il2cppdumper";
        public const string IL2CPP_LOAD_ERROR_MESSAGE = "Loading IL2CPP end up failure.\r\n" +
            "Make sure you selected right version of the game.";

        static readonly System.Text.Json.JsonSerializerOptions _scriptJsonOptions = new System.Text.Json.JsonSerializerOptions()
        {
            IncludeFields = true,
        };

        Il2Cpp? _il2Cpp;
        Metadata? _metadata;
        byte[] _gameAssemblyData;
        ScriptJson? _scriptJson;
        Dictionary<string, ScriptMethod>? _methodsDictionary;
        readonly string _dumpDirectory;
        readonly bool _interactive;
        public string OriginalAssemblySha256 { get; }
        internal string SavedAssemblySha256 { get; set; }
        public bool IsIl2cppLoaded { get => _il2Cpp != null; }
        public Il2Cpp IL2CPP
        {
            get
            {
                if (_il2Cpp == null)
                    throw new NullReferenceException("IL2CPP is not loaded or not loaded properly.");
                return _il2Cpp;
            }
        }
        public Metadata Metadata
        {
            get
            {
                if (_metadata == null)
                    throw new NullReferenceException("Metadata is not loaded or not loaded properly.");
                return _metadata;
            }
        }
        public ScriptJson ScriptJson
        {
            get
            {
                if (_scriptJson == null)
                    throw new NullReferenceException("ScriptJson is not loaded or not loaded properly.");
                return _scriptJson;
            }
        }

        /// <summary>
        /// Keys are methods names.
        /// </summary>
        public IReadOnlyDictionary<string, ScriptMethod> MethodsDictionary
        {
            get
            {
                if (_methodsDictionary == null)
                    throw new NullReferenceException("MethodsDictionary is not loaded or not loaded properly.");
                return _methodsDictionary;
            }
        }

        public Il2cppManager(byte[] gameAssemblyData, string? dumpDirectory = null, bool interactive = true)
        {
            _gameAssemblyData = gameAssemblyData;
            _dumpDirectory = Path.GetFullPath(dumpDirectory ?? IL2CPP_FOLDER);
            _interactive = interactive;
            OriginalAssemblySha256 = Convert.ToHexString(SHA256.HashData(gameAssemblyData));
            SavedAssemblySha256 = OriginalAssemblySha256;
        }

        private void LoadIl2cpp(byte[] gameAssemblyData, string metadataPath, double? metadataVersion = null)
        {
            if (gameAssemblyData == null || gameAssemblyData.Length == 0)
            {
                throw new ArgumentException("GameAssembly data is null or empty.");
            }
            if (!File.Exists(metadataPath))
            {
                throw new ArgumentException($"Couldn't find {Path.GetFileName(metadataPath)}. Please type correct path to your game.");
            }

            Config il2cppConfig = new Config();
            if (metadataVersion != null)
            {
                il2cppConfig.ForceIl2CppVersion = true;
                il2cppConfig.ForceVersion = metadataVersion.Value;
            }

            byte[] originalHash = XxHash128.Hash(gameAssemblyData);

            try
            {
                Il2CppDumperWorker.Init(gameAssemblyData, metadataPath, il2cppConfig, out _metadata, out _il2Cpp, _interactive);
            }
            catch (Exception ex) when (ex is not FileIsProtectedException)
            {
                _il2Cpp = null;
                throw new Il2cppInitException($"{IL2CPP_LOAD_ERROR_MESSAGE}{Environment.NewLine}{ex.Message}");
            }

            byte[] finalHash = XxHash128.Hash(gameAssemblyData);

            bool isHashTheSame = originalHash.SequenceEqual(finalHash);
            if (!isHashTheSame)
            {
                throw new Exception("The hash isn't the same. Looks like Il2cppDumper modified the GameAssembly data.");
            }
        }

        public void LoadIl2cpp(string metadataPath, double? metadataVersion = null)
        {
            LoadIl2cpp(_gameAssemblyData, metadataPath, metadataVersion);
        }

        public async Task LoadIl2cppAsync(string metadataPath, double? metadataVersion = null)
        {
            await Task.Run(() => LoadIl2cpp(metadataPath, metadataVersion));
        }

        public void DumpIl2cpp()
        {
            if (!Directory.Exists(_dumpDirectory))
                Directory.CreateDirectory(_dumpDirectory);

            Il2CppDumperWorker.Dump(Metadata, IL2CPP, _dumpDirectory + Path.DirectorySeparatorChar);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        public async Task DumpIl2cppAsync()
        {
            await Task.Run(() => DumpIl2cpp());
        }

        public void GetScriptJson()
        {
            string scriptPath = Path.Combine(_dumpDirectory, "script.json");
            if (!File.Exists(scriptPath))
                throw new Exception("Script file is not found. First dump il2cpp.");
            ScriptJson? scriptJson = JsonConvert.DeserializeObject<ScriptJson>(File.ReadAllText(scriptPath));
            if (scriptJson == null)
                throw new Exception("Couldn't deserialize JSON file.");
            _scriptJson = scriptJson;
        }

        public async Task GetScriptJsonAsync()
        {
            string scriptPath = Path.Combine(_dumpDirectory, "script.json");
            if (!File.Exists(scriptPath))
                throw new Exception("Script file is not found. First dump il2cpp.");
            ScriptJson? scriptJson;
            using (FileStream scriptStream = File.OpenRead(scriptPath))
            {
                scriptJson = await System.Text.Json.JsonSerializer.DeserializeAsync<ScriptJson>(scriptStream, _scriptJsonOptions);
            }
            if (scriptJson == null)
                throw new Exception("Couldn't deserialize JSON file.");
            _scriptJson = scriptJson;
        }

        public void GetMethodsDictionary()
        {
            _methodsDictionary = new Dictionary<string, ScriptMethod>();
            foreach (ScriptMethod method in ScriptJson.ScriptMethod)
            {
                string methodName = method.Name;
                if (_methodsDictionary.ContainsKey(methodName))
                    continue;
                _methodsDictionary.Add(methodName, method);
            }
        }

        public async Task GetMethodsDictionaryAsync()
        {
            await Task.Run(() => GetMethodsDictionary());
        }

        public void GenerateDummyDlls(string gamePath)
        {
            string gameDataFolder = Path.Combine(gamePath, "SCPSL_Data");
            Il2CppDumperWorker.GenerateDummyDll(Metadata, IL2CPP, gameDataFolder, "Managed");
        }

        public async Task GenerateDummyDllsAsync(string gamePath)
        {
            await Task.Run(() => GenerateDummyDlls(gamePath));
        }

        public void RemoveDumpFolder()
        {
            if (Directory.Exists(_dumpDirectory))
            {
                Directory.Delete(_dumpDirectory, true);
            }
        }
    }
}
