using Il2CppDumper;
using Iced.Intel;
using SCPSL_ModPatch.IL2Cpp;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SCPSL_ModPatch.PatchUtils
{
    internal class Patcher
    {
        Il2cppManager _il2cppManager;
        string _gameAssemblyPath;
        byte[] _gameAssemblyData;
        VersionRangeInfo _versionRangeInfo;
        readonly bool _interactive;
        readonly List<PatchOutcome> _patchOutcomes = new List<PatchOutcome>();

        public IReadOnlyList<PatchOutcome> PatchOutcomes { get => _patchOutcomes; }

        public Patcher(string gameAssemblyPath, byte[] gameAssemblyData, Il2cppManager il2CppManager, VersionRangeInfo versionRangeInfo, bool interactive = true)
        {
            _gameAssemblyPath = gameAssemblyPath;
            _gameAssemblyData = gameAssemblyData;
            _versionRangeInfo = versionRangeInfo;
            _il2cppManager = il2CppManager;
            _interactive = interactive;
        }

        public static async Task<byte[]> GetGameAssemblyDataAsync(string gameAssemblyPath)
        {
            if (!File.Exists(gameAssemblyPath))
            {
                throw new ArgumentException($"GameAssembly.dll was not found in:\r\n{Path.GetDirectoryName(gameAssemblyPath)}\r\n" +
                    "Choose the IL2CPP game folder containing SCPSL.exe, SCPSL_Data, and GameAssembly.dll.");
            }
            byte[] gameAssemblyData = await File.ReadAllBytesAsync(gameAssemblyPath);
            return gameAssemblyData;
        }

        private static ulong GetRVAOffset(Il2Cpp il2Cpp, ulong addr)
        {
            return il2Cpp.GetRVA(il2Cpp.MapRTVA(addr)) - addr;
        }

        private static int GetOffsetFromFuncName(Il2Cpp il2Cpp, string functionName, IReadOnlyDictionary<string, ScriptMethod> methodsDictionary)
        {
            if (methodsDictionary.ContainsKey(functionName))
            {
                ScriptMethod method = methodsDictionary[functionName];
                ulong address = method.Address;
                ulong offset = GetRawMethodOffset(il2Cpp, address);
                return Convert.ToInt32(offset);
            }
            return -1;
        }

        private static ulong GetRawMethodOffset(Il2Cpp il2Cpp, ulong address)
        {
            // ScriptMethod.Address is an RVA for PE images, not a raw file offset.
            if (il2Cpp is PE)
            {
                ulong rawOffset = il2Cpp.MapVATR(checked(il2Cpp.ImageBase + address));
                if (rawOffset == 0)
                    throw new InvalidDataException($"Method RVA 0x{address:X} does not map to the PE image.");
                return rawOffset;
            }
            return address - GetRVAOffset(il2Cpp, address);
        }

        private int GetOffsetFromFuncName(string functionName)
        {
            return GetOffsetFromFuncName(_il2cppManager.IL2CPP, functionName, _il2cppManager.MethodsDictionary);
        }

        private static void ReplaceLauncher(string gamePath, string cleanLauncherPath)
        {
            string nativeLauncherPath = @$"{gamePath}\SCPSL.exe";

            File.Copy(cleanLauncherPath, nativeLauncherPath, true);
        }

#if WINDOWS
        public bool ReplaceLauncher(string gamePath)
        {
            if (!IsPathValid(_versionRangeInfo.cleanLauncherPath))
            {
                if (!_interactive)
                    throw new InvalidDataException("Invalid launcher path in the patch info.");
                MessageBox.Show("Invalid launcher path in the patch info.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (!File.Exists(_versionRangeInfo.cleanLauncherPath))
            {
                if (!_interactive)
                    throw new FileNotFoundException("A local clean launcher is required in headless mode.", _versionRangeInfo.cleanLauncherPath);
                if (string.IsNullOrEmpty(_versionRangeInfo.cleanLauncherUrl))
                    return false;

                DialogResult launcherDialogResult = MessageBox.Show("The matching clean Unity launcher was not found. " +
                    "GameAssembly.dll has already been patched, but SCPSL.exe still needs to be replaced.\r\n\r\n" +
                    "Download the clean launcher now?", "Clean launcher required", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (launcherDialogResult == DialogResult.Yes)
                {
                    string cleanLauncherUrl = _versionRangeInfo.cleanLauncherUrl;
                    string cleanLauncherPath = _versionRangeInfo.cleanLauncherPath;
                    DownloadLauncher(cleanLauncherUrl, cleanLauncherPath);
                    if (!File.Exists(_versionRangeInfo.cleanLauncherPath))
                        return false;
                }
                else
                    return false;
            }
            ReplaceLauncher(gamePath, _versionRangeInfo.cleanLauncherPath);
            return true;
        }

        private static void DownloadLauncher(string cleanLauncherUrl, string cleanLauncherPath)
        {
            cleanLauncherPath = Path.GetFullPath(cleanLauncherPath);
            DownloadForm downloadForm = new DownloadForm(cleanLauncherUrl, cleanLauncherPath);
            downloadForm.ShowDialog();
        }
#endif

        private bool IsPathValid(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }
            return true;
        }

        private string PreparePatchData(string patchData)
        {
            StringBuilder patchDataSB = new StringBuilder(patchData);

            patchDataSB.Replace("[NOP]", "90");
            patchDataSB.Replace("[RET]", "C3");

            MatchCollection matches = Regex.Matches(patchDataSB.ToString(), @"\[FASM:(.*?)!\]");
            bool isFasmExisting = FASM.IsFasmExisting(_interactive);
            if (!isFasmExisting && matches.Count > 0)
            {
                throw new InvalidDataException("FASM Assembler binary was not found. No patches were applied.");
            }
            foreach (Match match in matches)
            {
                string fasmKeyword = match.Groups[0].Value;
                string mnemonics = match.Groups[1].Value;
                string hexInstructions;
                try
                {
                    hexInstructions = FASM.AssembleToHex(mnemonics, _interactive);
                    patchDataSB.Replace(fasmKeyword, hexInstructions);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Couldn't assemble ({fasmKeyword}): {ex.Message}", ex);
                }
            }

            patchDataSB.Replace(" ", string.Empty);
            patchDataSB.Replace("-", string.Empty);

            patchData = patchDataSB.ToString();
            return patchData;
        }

        /// <summary>
        /// Prepares a function patch without changing the GameAssembly.
        /// </summary>
        /// <param name="plan">Patch plan to add the validated write to.</param>
        /// <param name="functionOffset">Offset of the function.</param>
        /// <param name="patchMethod">Method patch information.</param>
        /// <param name="methodMap">Native method map, loaded when a pattern is used.</param>
        private void PlanFunctionPatch(BinaryPatchPlan plan, int functionOffset, PatchMethodInfo patchMethod, ref NativeMethodMap? methodMap)
        {
            string rawUserPatchData = patchMethod.patchData;
            string hexPatchData = PreparePatchData(rawUserPatchData);

            byte[] patchData;
            try
            {
                patchData = Convert.FromHexString(hexPatchData);
            }
            catch (Exception ex) when (ex is FormatException)
            {
                throw new FormatException($"The hex-coded patch data ({hexPatchData}) of ({patchMethod.name}) method aren't valid.");
            }
            int patchOffset;
            NativeMethodRange? methodRange = null;
            if (patchMethod.patchPattern != null)
            {
                if (patchMethod.patchOffset.HasValue)
                    throw new InvalidDataException("Use either patchPattern or patchOffset, not both.");
                if (methodMap == null)
                    _il2cppManager.RefreshMethodAddresses();
                int distinctAddresses = _il2cppManager.GetDistinctMethodAddresses(patchMethod.name).Count;
                if (distinctAddresses != 1)
                    throw new InvalidDataException("The method name resolves to more than one native address.");
                BytePattern pattern = BytePattern.Parse(patchMethod.patchPattern);
                methodMap ??= _il2cppManager.GetNativeMethodMap(_gameAssemblyData);
                methodRange = methodMap.GetMethodRange(functionOffset);
                NativeMethodRange range = methodRange.Value;
                int match = pattern.FindUniqueOffset(_gameAssemblyData.AsSpan(range.Offset, range.Length));
                patchOffset = checked(range.Offset + match + patchMethod.patternOffset);
            }
            else
            {
                if (patchMethod.patternOffset != 0)
                    throw new InvalidDataException("patternOffset requires patchPattern.");
                int relativeOffset = patchMethod.patchOffset ?? 0;
                if (relativeOffset < 0)
                    throw new InvalidDataException("patchOffset cannot be negative.");
                patchOffset = checked(functionOffset + relativeOffset);
            }

            int patchSize;
            if (patchMethod.patchSize == null)
            {
                patchSize = patchData.Length;
            }
            else
            {
                patchSize = patchMethod.patchSize.Value;
            }

            if (methodRange is NativeMethodRange bounds &&
                (patchOffset < bounds.Offset || (long)patchOffset + patchSize > (long)bounds.Offset + bounds.Length))
            {
                throw new InvalidDataException("The patch extends outside the matched native method.");
            }
            plan.Add(patchMethod.name, patchOffset, patchSize, patchData);
        }

        public void PatchGameAssembly()
        {
            _patchOutcomes.Clear();
            if (_versionRangeInfo.gameAssemblySha256 != null &&
                !string.Equals(_versionRangeInfo.gameAssemblySha256, _il2cppManager.OriginalAssemblySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GameAssembly.dll does not match this patch target's SHA-256; no patches were applied.");
            PatchMethodInfo[] patchMethods = _versionRangeInfo.methods.patchMethods;
            BinaryPatchPlan plan = new BinaryPatchPlan(_gameAssemblyData);
            NativeMethodMap? methodMap = null;
            List<PatchOutcome> outcomes = new List<PatchOutcome>();

            for (int i = 0; i < patchMethods.Length; i++)
            {
                PatchMethodInfo patchMethod = patchMethods[i];
                int functionOffset = GetOffsetFromFuncName(patchMethod.name);

                if (!string.IsNullOrEmpty(patchMethod.patchifnotexists))
                {
                    string notExistsFunctionName = patchMethod.patchifnotexists;
                    int notExistsFunctionOffset = GetOffsetFromFuncName(notExistsFunctionName);
                    bool doesFunctionNotExist = notExistsFunctionOffset < 0;
                    if (!doesFunctionNotExist)
                    {
                        outcomes.Add(new PatchOutcome(patchMethod.name, "skipped-fallback",
                            $"Fallback patch is not needed because ({notExistsFunctionName}) exists."));
                        continue;
                    }
                }
                if (functionOffset < 0)
                {
                    if (!patchMethod.nowarning)
                        throw new InvalidDataException($"Couldn't find ({patchMethod.name}); no patches were applied.");
                    outcomes.Add(new PatchOutcome(patchMethod.name, "skipped-optional",
                        "The method was not found."));
                    continue;
                }

                try
                {
                    PlanFunctionPatch(plan, functionOffset, patchMethod, ref methodMap);
                    outcomes.Add(new PatchOutcome(patchMethod.name, "applied", "The patch was applied."));
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Couldn't prepare patch ({patchMethod.name}): {ex.Message} No patches were applied.", ex);
                }
            }
            _il2cppManager.InvalidateNativeMethodMap();
            plan.Apply();
            _patchOutcomes.AddRange(outcomes);
        }

        /// <summary>
        /// Saves GameAssembly if the file has not changed since loading or the last save.
        /// </summary>
        public void SaveGameAssembly()
        {
            using (FileStream file = new FileStream(_gameAssemblyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using SHA256 hash = SHA256.Create();
                if (!string.Equals(Convert.ToHexString(hash.ComputeHash(file)), _il2cppManager.SavedAssemblySha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("GameAssembly.dll changed on disk after loading. Press Load game again before saving.");
                file.Position = 0;
                file.Write(_gameAssemblyData);
                file.SetLength(_gameAssemblyData.Length);
            }
            _il2cppManager.SavedAssemblySha256 = PatchTargetResolver.ComputeSha256(_gameAssemblyData);
        }

        /// <summary>
        /// Checks whether the default modern version method is absent.
        /// </summary>
        internal bool UsesLegacyVersionReader
        {
            get
            {
                return _versionRangeInfo.methods.gameVersionMethod?.name == "GameCore.Version$$.cctor" &&
                    !_il2cppManager.ScriptJson.ScriptMethod.Any(method => method.Name == "GameCore.Version$$.cctor");
            }
        }

        /// <summary>
        /// Gets a legacy release version from native code and metadata.
        /// </summary>
        internal LegacyReleaseVersionReader.Result GetLegacyReleaseVersion()
        {
            if (!UsesLegacyVersionReader)
                throw new InvalidOperationException("Legacy release detection requires the default modern version method to be absent.");

            ScriptJson script = _il2cppManager.ScriptJson;
            Dictionary<ulong, List<string>> labels = GetLegacyVersionLabels(script);
            Dictionary<ulong, string> strings = GetLegacyVersionStrings(script);
            HashSet<ulong> formatTargets = script.ScriptMethod.Where(method => method.Name == "System.String$$Format" &&
                method.Signature == "System_String_o* System_String__Format (System_String_o* format, System_Object_array* args, const MethodInfo* method);")
                .Select(method => method.Address).ToHashSet();
            NativeMethodMap methodMap = _il2cppManager.GetNativeMethodMap(_gameAssemblyData);
            string[] fields = GetLegacyVersionFields(_il2cppManager.Metadata);

            _il2cppManager.RefreshMethodAddresses();
            return LegacyReleaseVersionReader.Read(_gameAssemblyData, labels, strings, formatTargets,
                name => GetLegacyVersionMethod(name, methodMap), fields);
        }

        /// <summary>
        /// Gets method and metadata labels used by the legacy version reader.
        /// </summary>
        private static Dictionary<ulong, List<string>> GetLegacyVersionLabels(ScriptJson script)
        {
            Dictionary<ulong, List<string>> labels = new Dictionary<ulong, List<string>>();
            foreach (ScriptMethod method in script.ScriptMethod)
            {
                AddLegacyVersionLabel(labels, method.Address, method.Name);
            }
            foreach (ScriptMetadata item in script.ScriptMetadata)
            {
                AddLegacyVersionLabel(labels, item.Address, item.Name);
            }
            foreach (ScriptMetadataMethod item in script.ScriptMetadataMethod)
            {
                AddLegacyVersionLabel(labels, item.Address, item.Name);
            }
            return labels;
        }

        /// <summary>
        /// Adds a label without discarding other labels at the same address.
        /// </summary>
        private static void AddLegacyVersionLabel(Dictionary<ulong, List<string>> labels, ulong address, string name)
        {
            if (!labels.TryGetValue(address, out List<string>? names))
            {
                names = new List<string>();
                labels.Add(address, names);
            }
            names.Add(name);
        }

        /// <summary>
        /// Gets metadata strings and checks for conflicting values at one address.
        /// </summary>
        private static Dictionary<ulong, string> GetLegacyVersionStrings(ScriptJson script)
        {
            Dictionary<ulong, string> strings = new Dictionary<ulong, string>();
            foreach (ScriptString literal in script.ScriptString)
            {
                string value = literal.Value ?? string.Empty;
                if (strings.TryGetValue(literal.Address, out string? existing) && existing != value)
                    throw new InvalidDataException("A metadata string address has conflicting values.");
                strings[literal.Address] = value;
            }
            return strings;
        }

        /// <summary>
        /// Gets the bounded native method used by the legacy version reader.
        /// </summary>
        private LegacyReleaseVersionReader.Method GetLegacyVersionMethod(string name, NativeMethodMap methodMap)
        {
            IReadOnlyCollection<ulong> addresses = _il2cppManager.GetDistinctMethodAddresses(name);
            if (addresses.Count != 1)
                throw new InvalidDataException($"Legacy version method '{name}' is absent or ambiguous.");
            ulong address = addresses.Single();
            int rawOffset = checked((int)GetRawMethodOffset(_il2cppManager.IL2CPP, address));
            return new LegacyReleaseVersionReader.Method(name, address, methodMap.GetMethodRange(rawOffset));
        }

        /// <summary>
        /// Gets the network type's metadata fields used by the legacy version reader.
        /// </summary>
        private static string[] GetLegacyVersionFields(Metadata metadata)
        {
            Il2CppTypeDefinition[] definitions = metadata.typeDefs.Where(type => metadata.GetStringFromIndex(type.nameIndex) == "CustomNetworkManager" &&
                metadata.GetStringFromIndex(type.namespaceIndex) == "").ToArray();
            if (definitions.Length != 1)
                throw new InvalidDataException("Network metadata type is absent or ambiguous.");
            Il2CppTypeDefinition definition = definitions[0];
            if (definition.fieldStart < 0 || (long)definition.fieldStart + definition.field_count > metadata.fieldDefs.Length)
                throw new InvalidDataException("Network metadata field range is invalid.");
            return metadata.fieldDefs.Skip(definition.fieldStart).Take(definition.field_count)
                .Select(field => metadata.GetStringFromIndex(field.nameIndex)).ToArray();
        }

        /// <summary>
        /// Qualifies automatic modern detection against instruction-aligned byte stores
        /// inside the named native method, rather than arbitrary bytes at profile offsets.
        /// Explicit custom profiles retain their existing raw-offset reader.
        /// </summary>
        internal void VerifyGameVersionReader()
        {
            GameVersionMethodInfo? reader = _versionRangeInfo.methods.gameVersionMethod;
            if (reader == null || UsesLegacyVersionReader)
                return; // The legacy reader already requires bounded native and metadata evidence.
            IReadOnlyCollection<ulong> addresses = _il2cppManager.GetDistinctMethodAddresses(reader.name);
            if (addresses.Count != 1)
                throw new InvalidDataException("The native version method is absent or ambiguous.");
            int start = checked((int)GetRawMethodOffset(_il2cppManager.IL2CPP, addresses.Single()));
            NativeMethodRange range = _il2cppManager.GetNativeMethodMap(_gameAssemblyData).GetMethodRange(start);
            ReadOnlySpan<int> offsets = stackalloc int[] { reader.majorOffset, reader.minorOffset, reader.revisionOffset, reader.typeOffset };
            for (int i = 0; i < offsets.Length; i++)
                if (offsets[i] < 0 || offsets[i] >= range.Length || (i > 0 && offsets[i] <= offsets[i - 1]))
                    throw new InvalidDataException("Version-reader offsets are outside or out of order within the native method.");
            var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(_gameAssemblyData, range.Offset, range.Length));
            int found = 0;
            while (decoder.IP <= (ulong)offsets[3])
            {
                decoder.Decode(out Instruction instruction);
                if (instruction.IsInvalid)
                    throw new InvalidDataException("The native version method contains an invalid instruction before its version stores.");
                for (int i = 0; i < offsets.Length; i++)
                {
                    if (instruction.NextIP != (ulong)offsets[i] + 1)
                        continue;
                    ulong displacement = instruction.MemoryDisplacement64;
                    if (instruction.Mnemonic != Mnemonic.Mov || instruction.Op0Kind != OpKind.Memory ||
                        instruction.MemoryBase != Register.RCX || instruction.MemoryIndex != Register.None ||
                        instruction.MemorySize != MemorySize.UInt8 || instruction.Op1Kind != OpKind.Immediate8 ||
                        (i < 3 ? displacement != (ulong)i : displacement is not (3 or 4)))
                        throw new InvalidDataException("A version-reader offset is not an immediate version-field byte store.");
                    found |= 1 << i;
                }
            }
            if (found != 15)
                throw new InvalidDataException("Version-reader offsets do not identify four instruction-aligned native stores.");
        }

        public GameVersion GetGameVersion()
        {
            GameVersionMethodInfo? gameVersionMethod = _versionRangeInfo.methods.gameVersionMethod;
            if (gameVersionMethod == null)
                return new GameVersion();

            if (UsesLegacyVersionReader)
            {
                LegacyReleaseVersionReader.Result legacy = GetLegacyReleaseVersion();
                if (legacy.VerifiedVersion == null)
                    throw new GameVersionNotFoundException();
                Version version = new Version(legacy.VerifiedVersion);
                return new GameVersion(checked((byte)version.Major), checked((byte)version.Minor), checked((byte)version.Build));
            }

            int gameVersionOffset = GetOffsetFromFuncName(gameVersionMethod.name);
            if (gameVersionOffset < 0)
            {
                throw new GameVersionNotFoundException();
            }
            GameVersion gameVersion = new GameVersion(
                _gameAssemblyData[gameVersionOffset + gameVersionMethod.majorOffset],
                _gameAssemblyData[gameVersionOffset + gameVersionMethod.minorOffset],
                _gameAssemblyData[gameVersionOffset + gameVersionMethod.revisionOffset],
                _gameAssemblyData[gameVersionOffset + gameVersionMethod.typeOffset]);
            return gameVersion;
        }

        public void ChangeGameVersion(GameVersion version)
        {
            if (UsesLegacyVersionReader || !version.type.HasValue)
                throw new NotSupportedException("Legacy release-version detection is read-only; changing these versions is not supported.");
            GameVersionMethodInfo? gameVersionMethod = _versionRangeInfo.methods.gameVersionMethod;
            if (gameVersionMethod == null)
                return;

            int gameVersionOffset = GetOffsetFromFuncName(gameVersionMethod.name);
            if (gameVersionOffset < 0)
                throw new GameVersionNotFoundException();
            _il2cppManager.InvalidateNativeMethodMap();
            _gameAssemblyData[gameVersionOffset + gameVersionMethod.majorOffset] = version.major;
            _gameAssemblyData[gameVersionOffset + gameVersionMethod.minorOffset] = version.minor;
            _gameAssemblyData[gameVersionOffset + gameVersionMethod.revisionOffset] = version.revision;
            _gameAssemblyData[gameVersionOffset + gameVersionMethod.typeOffset] = (byte)version.type.Value;
        }

        public void AutoFindGameVersionOffsets()
        {
            GameVersionMethodInfo? gameVersionMethod = _versionRangeInfo.methods.gameVersionMethod;
            if (gameVersionMethod == null)
                return;

            if (UsesLegacyVersionReader)
            {
                GetLegacyReleaseVersion();
                return;
            }

            int gameVersionOffset = GetOffsetFromFuncName(gameVersionMethod.name);
            if (gameVersionOffset < 0)
                throw new GameVersionNotFoundException();
            AutoFindGameVersionOffsets(gameVersionOffset);
        }

        public void AutoFindGameVersionOffsets(int gameVersionOffset)
        {
            GameVersionMethodInfo? gameVersionMethod = _versionRangeInfo.methods.gameVersionMethod;
            if (gameVersionMethod == null)
                return;

            byte majorFieldOffset = 0;
            byte minorFieldOffset = 1;
            byte revisionFieldOffset = 2;
            byte typeFieldOffset = 4;

            byte[] fieldsOffsets = new byte[] { majorFieldOffset, minorFieldOffset, revisionFieldOffset, typeFieldOffset };
            int[] fieldsValuesOffsets = new int[fieldsOffsets.Length];

            int findOffset = checked((int)GetPatternOffset(gameVersionOffset));
            for (int i = 0; i < fieldsOffsets.Length; i++)
            {
                fieldsValuesOffsets[i] = GetVersionStaticFieldValueOffset(fieldsOffsets[i], findOffset, out int newFindOffset);
                findOffset = newFindOffset;
            }

            gameVersionMethod.majorOffset = fieldsValuesOffsets[0] - gameVersionOffset;
            gameVersionMethod.minorOffset = fieldsValuesOffsets[1] - gameVersionOffset;
            gameVersionMethod.revisionOffset = fieldsValuesOffsets[2] - gameVersionOffset;
            gameVersionMethod.typeOffset = fieldsValuesOffsets[3] - gameVersionOffset;
        }

        private long GetPatternOffset(int gameVersionOffset)
        {
            byte[] patternInstruction = new byte[] { 0x48, 0x8B, 0x88, 0xB8, 0x00, 0x00, 0x00 }; // mov rcx, [rax+0B8h]
            long patternOffset = _gameAssemblyData.IndexOf(patternInstruction, gameVersionOffset);
            if (patternOffset == -1)
                throw new GameVersionNotFoundException("The pattern for auto game version search wasn't found.");
            return patternOffset;
        }

        private int GetVersionStaticFieldValueOffset(byte staticFieldOffset, int findOffset, out int newFindOffset)
        {
            byte[] instructionBytes = GetVersionStaticFieldInstructionBytes(staticFieldOffset);
            int instructionLength = instructionBytes.Length;
            int instructionOffset = checked((int)_gameAssemblyData.IndexOf(instructionBytes, findOffset));
            if (instructionOffset == -1)
                throw new GameVersionNotFoundException($"The instruction offset for auto game version search with field offset ({staticFieldOffset}) wasn't found.");

            int staticFieldValueOffset = instructionOffset + instructionLength;

            newFindOffset = staticFieldValueOffset + 1;

            return staticFieldValueOffset;
        }

        private static byte[] GetVersionStaticFieldInstructionBytes(byte staticFieldOffset)
        {
            switch (staticFieldOffset)
            {
                case 0:
                    return new byte[] { 0xC6, 0x01 }; // mov byte ptr [rcx], {value}
                default:
                    return new byte[] { 0xC6, 0x41, staticFieldOffset }; // mov byte ptr [rcx+{offset}], {value}
            }
        }
    }
}
