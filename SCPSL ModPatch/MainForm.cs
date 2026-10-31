using Newtonsoft.Json;
using SCPSL_ModPatch.IL2Cpp;
using SCPSL_ModPatch.PatchUtils;

namespace SCPSL_ModPatch
{
    public partial class MainForm : Form
    {
        const string PATCHINFO_FILENAME = @".\patchinfo.json";

        const string IL2CPP_IS_NOT_LOADED_MESSAGE = "The game is not loaded. Please press Load game first.";
        const string IL2CPP_IS_LOADING_MESSAGE = "The game is loading. Please wait and try again.";
        const string GAME_PATH_IS_EMPTY_MESSAGE = "Choose your game folder in Open settings, then press Load game.";
        const string GAME_PATH_IS_INVALID_MESSAGE = "The game folder does not exist. In Open settings, choose the folder containing SCPSL.exe and SCPSL_Data.";
        const string IL2CPP_LOADED_FOR_DIFFERENT_VERSIONS_MESSAGE = "The loaded game does not match the selected profile or folder. Please load the game again.";
        const string GAME_VERSION_IS_NULL = "Can't change the game version because there is no information in the patch info or it was loaded " +
            "with an error.";

        Configuration _config;
        PatchInfo _patchInfo;

        byte[] _gameAssemblyData = Array.Empty<byte>();
        string GamePath { get => _config.GameFolder_Path; }
        string GameAssemblyPath { get => @$"{GamePath}\GameAssembly.dll"; }

        Il2cppManager _il2CppManager = new Il2cppManager(Array.Empty<byte>());
        PatchSession? _patchSession;
        GameVersion? _gameVersion;
        readonly string _defaultGameVersionString;
        bool _isIl2cppLoading = false;
        bool _isPatchInfoUpdating = false;

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

        public MainForm()
        {
            InitializeComponent();
            _config = SettingsForm.GetConfiguration();
            _defaultGameVersionString = versionTextBox.Lines[1];
            if (_config.CustomPatchInfoEnable)
                _patchInfo = GetPatchInfo(_config.CustomPatchInfoPath);
            else if (_config.AutoUpdatePatchInfo && !File.Exists(PATCHINFO_FILENAME))
                _patchInfo = new PatchInfo();
            else
                _patchInfo = GetPatchInfo(PATCHINFO_FILENAME);

            UpdateVersionComboBox();
            versionComboBox.SelectedIndexChanged += (_, _) => UpdateControls();
            UpdateControls();
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
                MessageBox.Show("No custom patch definitions file is selected. In Open settings, choose a patchinfo.json file or disable custom patch definitions, then restart.",
                    "Patch definitions not selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return new PatchInfo();
            }
            if (!File.Exists(patchInfoPath))
            {
                MessageBox.Show($"Patch definitions were not found:\r\n{patchInfoPath}\r\n\r\nRestore the bundled patchinfo.json or use the download button. For custom definitions, check the path in Open settings, then restart.",
                    "Patch definitions not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return new PatchInfo();
            }
            try
            {
                PatchInfo patchInfo = JsonConvert.DeserializeObject<PatchInfo>(File.ReadAllText(patchInfoPath))
                    ?? throw new InvalidDataException("The patch definitions file is empty.");
                PatchTargetResolver.Validate(patchInfo);
                return patchInfo;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load patch definitions from:\r\n{patchInfoPath}\r\n\r\n{ex.Message}\r\n\r\n" +
                    "Restore the bundled patchinfo.json or download it again. For a custom file, check its path and contents in Open settings, then restart.",
                    "Patch definitions could not be loaded", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return new PatchInfo();
            }
        }

        private void UpdateVersionComboBox()
        {
            if (versionComboBox.Items.Count > 0)
                versionComboBox.Items.Clear();
            versionComboBox.Items.AddRange(_patchInfo.versionRanges);
        }

        private void UpdateControls()
        {
            bool busy = _isIl2cppLoading || _isPatchInfoUpdating;
            il2cppButton.Enabled = !busy && _patchInfo.versionRanges.Length > 0;
            versionComboBox.Enabled = !busy;
            openSettingsButton.Enabled = !busy;
            updatePatchInfoButton.Enabled = !busy && !_config.CustomPatchInfoEnable;
            bool loaded = !busy && _il2cppLoadedVersionRange != null && IsIl2cppLoadedForSelectedVersionRange();
            patchButton.Enabled = loaded;
            changeVersionButton.Enabled = loaded && _gameVersion?.type.HasValue == true;
            patchInfoToolTip.SetToolTip(updatePatchInfoButton, _config.CustomPatchInfoEnable
                ? "Disable custom patch definitions in settings to download from GitHub."
                : "Download the latest patch definitions from GitHub. This does not update the program.");
            patchInfoToolTip.SetToolTip(openSettingsButton, string.IsNullOrWhiteSpace(GamePath)
                ? "Choose the folder containing SCPSL.exe and SCPSL_Data."
                : GamePath);
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
            UpdateControls();
            try
            {
                _patchInfo = await Updater.UpdatePatchInfoAsync(PATCHINFO_FILENAME);
                _patchSession = null;
                _il2cppLoadedVersionRange = null;
                _il2cppLoadedAssemblyPath = null;
                _gameVersion = null;
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
                UpdateControls();
            }
        }

        private void openSettingsButton_Click(object sender, EventArgs e)
        {
            using SettingsForm settingsForm = new SettingsForm();
            settingsForm.ShowDialog(this);
            _config = SettingsForm.GetConfiguration();
            UpdateControls();
        }

        private async void il2cppButton_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(GamePath) || !Directory.Exists(GamePath))
            {
                MessageBox.Show(GAME_PATH_IS_INVALID_MESSAGE, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _isIl2cppLoading = true;
            UpdateControls();
            var initButtonText = il2cppButton.Text;
            il2cppButton.Text = "Loading game...";
            _il2cppLoadedVersionRange = null;
            _il2cppLoadedAssemblyPath = null;
            _patchSession = null;
            _gameVersion = null;
            ChangeVersionTextBoxLines(1, _defaultGameVersionString);

            try
            {
                PatchSession session = await PatchSession.LoadAsync(GamePath, _patchInfo,
                    versionComboBox.SelectedItem as VersionRangeInfo, interactive: true,
                    createDummyDlls: _config.CreateManagedDummyDllFolder);
                _gameAssemblyData = session.AssemblyData;
                _patchSession = session;
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
                    MessageBox.Show(ex.Message, "Game unpacking failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Game load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                il2cppButton.Text = initButtonText;
                _isIl2cppLoading = false;
                UpdateControls();
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

            if (!_il2CppManager.IsIl2cppLoaded || _il2cppLoadedVersionRange == null || _patchSession == null)
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

            Patcher patcher = new Patcher(GameAssemblyPath, _gameAssemblyData, _il2CppManager, Il2cppLoadedVersionRange);
            bool saveStarted = false;
            try
            {
                patcher.PatchGameAssembly();
                saveStarted = true;
                _patchSession!.SaveGameAssemblyAndMetadata(patcher);
                bool launcherReplaced = patcher.ReplaceLauncher(GamePath);
                MessageBox.Show(launcherReplaced
                    ? "GameAssembly.dll was patched and SCPSL.exe was replaced with the clean launcher.\r\n\r\nNative game files are not backed up automatically."
                    : "GameAssembly.dll was patched, but SCPSL.exe was not replaced. Patching is incomplete.\r\n\r\nRestore your original files before retrying, or supply the matching clean launcher and replace SCPSL.exe manually.",
                    launcherReplaced ? "Patch complete" : "Launcher replacement skipped", MessageBoxButtons.OK,
                    launcherReplaced ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show((saveStarted
                    ? "Saving the patch or replacing the launcher failed. Some game files may have changed; restore your original files before retrying.\r\n\r\n"
                    : "Patch preparation failed. No game files were saved.\r\n\r\n") + ex.Message,
                    "Patch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _il2cppLoadedVersionRange = null;
                _patchSession = null;
                UpdateControls();
            }
        }

        private void changeVersionButton_Click(object sender, EventArgs e)
        {
            if (!LoadedGameValidation())
                return;

            if (_gameVersion == null)
            {
                MessageBox.Show(GAME_VERSION_IS_NULL, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                _patchSession!.SaveGameAssemblyAndMetadata(patcher);
            }
            catch (Exception ex)
            {
                _il2cppLoadedVersionRange = null;
                _patchSession = null;
                MessageBox.Show(ex.Message, "Version change failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateControls();
                return;
            }
            _gameVersion = changeVersionForm.version;
            ChangeVersionTextBoxLines(1, _gameVersion.Value.ToString());
        }

        private bool IsIl2cppLoadedForSelectedVersionRange()
        {
            if (_il2cppLoadedVersionRange == versionComboBox.SelectedItem &&
                string.Equals(_il2cppLoadedAssemblyPath, GameAssemblyPath, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
    }
}
