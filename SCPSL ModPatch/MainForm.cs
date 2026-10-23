using Newtonsoft.Json;
using SCPSL_ModPatch.IL2Cpp;
using SCPSL_ModPatch.PatchUtils;
using System.Text.Json;

namespace SCPSL_ModPatch
{
    public partial class MainForm : Form
    {
        const string PATCHINFO_FILENAME = @".\patchinfo.json";
        const string PATCHINFO_TEMPLATE_FILENAME = @".\patchinfo_template.json";

        const string IL2CPP_IS_NOT_LOADED_MESSAGE = "The game is not loaded. Please press Load game first.";
        const string IL2CPP_IS_LOADING_MESSAGE = "The game is loading. Please wait and try again.";
        const string GAME_PATH_IS_EMPTY_MESSAGE = "Game path is empty. Please type valid game path in the settings.";
        const string GAME_PATH_IS_INVALID_MESSAGE = "Game path is invalid. Please type valid game path in the settings.";
        const string IL2CPP_LOADED_FOR_DIFFERENT_VERSIONS_MESSAGE = "The loaded game does not match the selected profile or folder. Please load the game again.";
        const string GAME_VERSION_METHOD_IS_NOT_FOUND_MESSAGE = "Game version is not found in \"GameAssembly.dll\".\r\n" +
                    "Try to find game version data in \"global-metadata.dat\".";
        const string GAME_VERSION_IS_NULL = "Can't change the game version because there is no information in the patch info or it was loaded " +
            "with an error.";

        Configuration _config;
        PatchInfo _patchInfo;

        byte[] _gameAssemblyData = Array.Empty<byte>();
        string GamePath { get => _config.GameFolder_Path; }
        string GameAssemblyPath { get => @$"{GamePath}\GameAssembly.dll"; }

        Il2cppManager _il2CppManager = new Il2cppManager(Array.Empty<byte>());
        GameVersion? _gameVersion;
        bool _isGameVersionNotFound = false;
        readonly string _defaultGameVersionString;
        bool _isIl2cppLoading = false;
        bool _isPatchInfoUpdating = false;
        MonoPatchSession? _monoSession;

        VersionRangeInfo SelectedVersionRange { get => (VersionRangeInfo)versionComboBox.SelectedItem; }
        VersionRangeInfo? _il2cppLoadedVersionRange;
        string? _il2cppLoadedAssemblyPath;
        VersionRangeInfo Il2cppLoadedVersionRange
        {
            get
            {
                if (_il2cppLoadedVersionRange == null)
                    throw new NullReferenceException("IL2CPP is not loaded.");
                return _il2cppLoadedVersionRange;
            }
        }

        public static JsonSerializerOptions JsonOptions { get; } = new()
        {
            IncludeFields = true,
        };

        public MainForm()
        {
            InitializeComponent();
            il2cppButton.Text = "Load game";
            _config = SettingsForm.GetConfiguration();
            _defaultGameVersionString = versionTextBox.Lines[1];
#if DEBUG
            GeneratePatchInfoTemplate();
#endif
            if (_config.CustomPatchInfoEnable)
                _patchInfo = GetPatchInfo(_config.CustomPatchInfoPath);
            else if (_config.AutoUpdatePatchInfo && !File.Exists(PATCHINFO_FILENAME))
                _patchInfo = new PatchInfo();
            else
                _patchInfo = GetPatchInfo(PATCHINFO_FILENAME);

            UpdateVersionComboBox();
            UpdatePatchInfoButton();
        }

        private async void MainForm_Shown(object? sender, EventArgs e)
        {
            if (!_config.CustomPatchInfoEnable && _config.AutoUpdatePatchInfo && !File.Exists(PATCHINFO_FILENAME))
                await UpdatePatchInfoAsync();
        }

        private PatchInfo GetPatchInfo(string patchInfoPath)
        {
            if (string.IsNullOrEmpty(patchInfoPath))
            {
                MessageBox.Show("Patch info path is empty.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return new PatchInfo();
            }
            if (!File.Exists(patchInfoPath))
            {
                MessageBox.Show($"Patch info (\"{Path.GetFileName(patchInfoPath)}\") file was not found.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return new PatchInfo();
            }
            PatchInfo? patchInfo = JsonConvert.DeserializeObject<PatchInfo>(File.ReadAllText(patchInfoPath));
            if (patchInfo == null)
                throw new Exception("Couldn't deserialize JSON file.");
            PatchTargetResolver.Validate(patchInfo);
            return patchInfo;
        }

        private void GeneratePatchInfoTemplate()
        {
            PatchInfo patchInfo = new PatchInfo();
            patchInfo.versionRanges = new VersionRangeInfo[1];
            patchInfo.versionRanges[0] = new VersionRangeInfo();
            patchInfo.versionRanges[0].versionRange = "TEMPLATE_PATCHINFO";
            patchInfo.versionRanges[0].cleanLauncherUrl = string.Empty;
            patchInfo.versionRanges[0].methods.patchMethods = new PatchMethodInfo[1];
            patchInfo.versionRanges[0].methods.gameVersionMethod = new GameVersionMethodInfo();
            patchInfo.versionRanges[0].methods.patchMethods[0] = new PatchMethodInfo();
            patchInfo.versionRanges[0].methods.patchMethods[0].patchOffset = 0;
            patchInfo.versionRanges[0].methods.patchMethods[0].patchData = Convert.ToHexString(new byte[] { 0 });
            patchInfo.versionRanges[0].methods.patchMethods[0].patchSize = 0;
            string patchInfoJson = JsonConvert.SerializeObject(patchInfo, Formatting.Indented);
            File.WriteAllText(PATCHINFO_TEMPLATE_FILENAME, patchInfoJson);
        }

        private void UpdateVersionComboBox()
        {
            if (versionComboBox.Items.Count > 0)
                versionComboBox.Items.Clear();
            versionComboBox.Items.AddRange(_patchInfo.versionRanges);
        }

        private void UpdatePatchInfoButton()
        {
            updatePatchInfoButton.Enabled = !_isIl2cppLoading && !_isPatchInfoUpdating && !_config.CustomPatchInfoEnable;
            patchInfoToolTip.SetToolTip(updatePatchInfoButton, _config.CustomPatchInfoEnable
                ? "Disable custom patch info in settings to download from GitHub"
                : "Download patch info from GitHub");
        }

        private async void updatePatchInfoButton_Click(object sender, EventArgs e)
        {
            await UpdatePatchInfoAsync();
        }

        private async Task UpdatePatchInfoAsync()
        {
            if (_isIl2cppLoading || _isPatchInfoUpdating || _config.CustomPatchInfoEnable)
                return;

            _isPatchInfoUpdating = true;
            UpdatePatchInfoButton();
            il2cppButton.Enabled = false;
            versionComboBox.Enabled = false;
            openSettingsButton.Enabled = false;
            patchButton.Enabled = false;
            changeVersionButton.Enabled = false;
            try
            {
                _patchInfo = await Updater.UpdatePatchInfoAsync(PATCHINFO_FILENAME);
                _il2cppLoadedVersionRange = null;
                _monoSession = null;
                _il2cppLoadedAssemblyPath = null;
                _gameVersion = null;
                _isGameVersionNotFound = false;
                ChangeVersionTextBoxLines(1, _defaultGameVersionString);
                UpdateVersionComboBox();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Patch info update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _isPatchInfoUpdating = false;
                UpdatePatchInfoButton();
                il2cppButton.Enabled = true;
                versionComboBox.Enabled = true;
                openSettingsButton.Enabled = true;
                patchButton.Enabled = true;
                changeVersionButton.Enabled = true;
            }
        }

        private void openSettingsButton_Click(object sender, EventArgs e)
        {
            SettingsForm settingsForm = new SettingsForm();
            settingsForm.ShowDialog();
            _config = SettingsForm.GetConfiguration();
            UpdatePatchInfoButton();
        }

        private async void il2cppButton_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(GamePath) || !Directory.Exists(GamePath))
            {
                MessageBox.Show(GAME_PATH_IS_INVALID_MESSAGE, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _isIl2cppLoading = true;
            UpdatePatchInfoButton();
            il2cppButton.Enabled = false;
            versionComboBox.Enabled = false;
            openSettingsButton.Enabled = false;
            var initButtonText = il2cppButton.Text;
            il2cppButton.Text = "Loading game...";
            _il2cppLoadedVersionRange = null;
            _monoSession = null;
            _il2cppLoadedAssemblyPath = null;
            _gameVersion = null;
            _isGameVersionNotFound = false;
            ChangeVersionTextBoxLines(1, _defaultGameVersionString);

            try
            {
                if (MonoPatchSession.IsMonoGame(GamePath, _patchInfo))
                {
                    VersionRangeInfo? selected = versionComboBox.SelectedItem as VersionRangeInfo;
                    _monoSession = await MonoPatchSession.LoadAsync(GamePath, _patchInfo,
                        selected != null && PatchTargetResolver.GetBackend(selected) == "mono" ? selected : null);
                    _il2cppLoadedVersionRange = _monoSession.Target;
                    _il2cppLoadedAssemblyPath = _monoSession.AssemblyPath;
                    versionComboBox.SelectedItem = _monoSession.Target;
                    ChangeVersionTextBoxLines(1, _monoSession.VersionLabel ?? _monoSession.Target.ToString());
                    return;
                }
                PatchSession session = await PatchSession.LoadAsync(GamePath, _patchInfo,
                    versionComboBox.SelectedItem is VersionRangeInfo initial && PatchTargetResolver.GetBackend(initial) == "il2cpp" ? initial : null, interactive: true,
                    createDummyDlls: _config.CreateManagedDummyDllFolder);
                _gameAssemblyData = session.AssemblyData;
                _il2CppManager = session.Manager;
                _il2cppLoadedVersionRange = session.Target;
                _il2cppLoadedAssemblyPath = GameAssemblyPath;
                versionComboBox.SelectedItem = session.Target;
                _gameVersion = session.Target.methods.gameVersionMethod == null ? null : session.DetectedVersion;
                if (session.DetectedVersion.HasValue)
                    ChangeVersionTextBoxLines(1, session.DetectedVersion.Value.ToString());
            }
            catch (FileIsProtectedException)
            {
                try
                {
                    Unpacker unpacker = new Unpacker(_config.Unlicense_Path, GameAssemblyPath);
                    unpacker.UnpackGameAssembly();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "IL2CPP load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                _isGameVersionNotFound = ex is GameVersionNotFoundException;
                MessageBox.Show(ex.Message, "Game load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                il2cppButton.Text = initButtonText;
                il2cppButton.Enabled = true;
                versionComboBox.Enabled = true;
                openSettingsButton.Enabled = true;
                _isIl2cppLoading = false;
                UpdatePatchInfoButton();
            }
        }

        private void ChangeVersionTextBoxLines(int i, string value)
        {
            var textLines = versionTextBox.Lines;
            textLines[i] = value;
            versionTextBox.Lines = textLines;
        }

        private bool Il2cppValidation()
        {
            if (_isIl2cppLoading)
            {
                MessageBox.Show(IL2CPP_IS_LOADING_MESSAGE, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if ((_monoSession == null && !_il2CppManager.IsIl2cppLoaded) || _il2cppLoadedVersionRange == null)
            {
                MessageBox.Show(IL2CPP_IS_NOT_LOADED_MESSAGE, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        private bool LoadedGameValidation()
        {
            if (!Il2cppValidation())
                return false;

            if (string.IsNullOrEmpty(GamePath))
            {
                MessageBox.Show(GAME_PATH_IS_EMPTY_MESSAGE, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (!IsIl2cppLoadedForSelectedVersionRange())
            {
                MessageBox.Show(IL2CPP_LOADED_FOR_DIFFERENT_VERSIONS_MESSAGE, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        private void patchButton_Click(object sender, EventArgs e)
        {
            if (!LoadedGameValidation())
                return;

            if (_monoSession != null)
            {
                try
                {
                    byte[] rewritten = _monoSession.Prepare();
                    bool changed = PatchTargetResolver.ComputeSha256(rewritten) != _monoSession.OriginalSha256;
                    _monoSession.Save(rewritten);
                    MessageBox.Show(changed ? "The managed client assembly was patched. Original backup:\r\n" + _monoSession.BackupPath :
                        "This build does not require client authentication patches. No files were changed.",
                        "Mono patch complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Mono patch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    _monoSession = null;
                    _il2cppLoadedVersionRange = null;
                }
                return;
            }

            Patcher patcher = new Patcher(GameAssemblyPath, _gameAssemblyData, _il2CppManager, Il2cppLoadedVersionRange);
            try
            {
                patcher.PatchGameAssembly();
                patcher.SaveGameAssembly();
            }
            catch (Exception ex)
            {
                _il2cppLoadedVersionRange = null;
                MessageBox.Show(ex.Message, "Patch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            patcher.ReplaceLauncher(GamePath);

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private void changeVersionButton_Click(object sender, EventArgs e)
        {
            if (!LoadedGameValidation())
                return;

            if (_monoSession != null)
            {
                MessageBox.Show("Mono version detection is read-only. Select patches through the manifest's assembly hash or version targets.",
                    "Version detection is read-only", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_gameVersion == null)
            {
                MessageBox.Show(GAME_VERSION_IS_NULL, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (_isGameVersionNotFound)
            {
                MessageBox.Show(GAME_VERSION_METHOD_IS_NOT_FOUND_MESSAGE, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!_gameVersion.Value.type.HasValue)
            {
                MessageBox.Show("This legacy release version is detected from native code and metadata. Changing it is not supported.",
                    "Version detection is read-only", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ChangeVersionForm changeVersionForm;
            try
            {
                changeVersionForm = new ChangeVersionForm(_gameVersion.Value);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            changeVersionForm.ShowDialog();
            changeVersionForm.Dispose();

            if (!changeVersionForm.versionChanged)
                return;

            Patcher patcher = new Patcher(GameAssemblyPath, _gameAssemblyData, _il2CppManager, Il2cppLoadedVersionRange);
            try
            {
                patcher.ChangeGameVersion(changeVersionForm.version);
                patcher.SaveGameAssembly();
            }
            catch (Exception ex)
            {
                _il2cppLoadedVersionRange = null;
                MessageBox.Show(ex.Message, "Version change failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _gameVersion = changeVersionForm.version;
            ChangeVersionTextBoxLines(1, _gameVersion.Value.ToString());
        }

        private bool IsIl2cppLoadedForSelectedVersionRange()
        {
            if (_il2cppLoadedVersionRange == SelectedVersionRange &&
                string.Equals(_il2cppLoadedAssemblyPath, _monoSession == null ? GameAssemblyPath :
                    MonoPatchSession.ResolveAssemblyPath(GamePath, _monoSession.Target), StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
    }
}
