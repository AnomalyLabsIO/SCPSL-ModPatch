namespace SCPSL_ModPatch
{
    public partial class SettingsForm : Form
    {
        const string CONFIG_FILENAME = @".\config.json";
        Configuration _config;
        bool _requireRestart;

        public SettingsForm()
        {
            InitializeComponent();
            _config = GetConfiguration();
            UpdateSettings(_config);
            customPatchInfoCheckBox.CheckedChanged += (_, _) => UpdatePatchInfoControls();
            UpdatePatchInfoControls();
        }

        public static Configuration GetConfiguration()
        {
            if (!File.Exists(CONFIG_FILENAME))
                return new Configuration();
            return Configuration.GetConfiguration(CONFIG_FILENAME);
        }

        private void UpdateSettings(Configuration config)
        {
            gamePathTextBox.Text = config.GameFolder_Path;

            unlicenseTextBox.Text = config.Unlicense_Path;

            autoUpdatePatchInfoCheckBox.Checked = config.AutoUpdatePatchInfo;

            createManagedFolderCheckBox.Checked = config.CreateManagedDummyDllFolder;

            customPatchInfoCheckBox.Checked = config.CustomPatchInfoEnable;

            customPatchInfoPathTextBox.Text = config.CustomPatchInfoPath;
        }

        private void UpdatePatchInfoControls()
        {
            bool custom = customPatchInfoCheckBox.Checked;
            customPatchInfoPathTextBox.Enabled = custom;
            browseCustomPatchInfoPathButton.Enabled = custom;
            autoUpdatePatchInfoCheckBox.Enabled = !custom;
        }

        private void ApplySettings()
        {
            _config.GameFolder_Path = gamePathTextBox.Text;

            _config.Unlicense_Path = unlicenseTextBox.Text;

            _config.AutoUpdatePatchInfo = autoUpdatePatchInfoCheckBox.Checked;

            _config.CreateManagedDummyDllFolder = createManagedFolderCheckBox.Checked;

            if (_config.CustomPatchInfoEnable != customPatchInfoCheckBox.Checked)
                _requireRestart = true;
            _config.CustomPatchInfoEnable = customPatchInfoCheckBox.Checked;

            if (_config.CustomPatchInfoPath != customPatchInfoPathTextBox.Text)
                _requireRestart = true;
            _config.CustomPatchInfoPath = customPatchInfoPathTextBox.Text;

            _config.SaveConfiguration(CONFIG_FILENAME);

            if (_requireRestart)
                ShowRestartWarning();
        }

        private void ResetSettings()
        {
            UpdateSettings(new Configuration());
            ApplySettings();
        }

        private void ShowRestartWarning()
        {
            MessageBox.Show("Restart ModPatch to load the selected patch definitions.", "Restart required", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void applyButton_Click(object sender, EventArgs e)
        {
            try
            {
                ApplySettings();
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Settings could not be saved.\r\n\r\n{ex.Message}", "Settings not saved",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void resetButton_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Reset all settings, including the game folder and custom patch definitions?",
                "Reset settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                ResetSettings();
        }

        private void browseGamePathButton_Click(object sender, EventArgs e)
        {
            if (folderBrowserDialog.ShowDialog() == DialogResult.Cancel)
                return;
            gamePathTextBox.Text = folderBrowserDialog.SelectedPath;
        }

        private void browseUnlicensePathButton_Click(object sender, EventArgs e)
        {
            if (openExeFileDialog.ShowDialog() == DialogResult.Cancel)
                return;
            unlicenseTextBox.Text = openExeFileDialog.FileName;
        }

        private void browseCustomPatchInfoPathButton_Click(object sender, EventArgs e)
        {
            if (openJsonFileDialog.ShowDialog() == DialogResult.Cancel)
                return;
            customPatchInfoPathTextBox.Text = openJsonFileDialog.FileName;
        }
    }
}
