namespace SCPSL_ModPatch
{
    public partial class ChangeVersionForm : Form
    {
        public GameVersion version;
        public bool versionChanged = false;

        public ChangeVersionForm(GameVersion version)
        {
            InitializeComponent();
            versionTypeBox.Items.AddRange(Enum.GetNames(typeof(GameVersion.VersionType)));
            this.version = version;
            versionTextBox.Text = version.ToString();
            if (!version.type.HasValue || (byte)version.type.Value >= versionTypeBox.Items.Count)
            {
                throw new ArgumentException("Invalid version type. Probably game version was not loaded properly.");
            }
            versionTypeBox.SelectedIndex = (byte)version.type.Value;
        }

        private void changeButton_Click(object sender, EventArgs e)
        {
            try
            {
                version = new GameVersion(versionTextBox.Text, version.type!.Value);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            versionChanged = true;
            Close();
        }

        private void versionTypeBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            version.type = (GameVersion.VersionType)versionTypeBox.SelectedIndex;
        }
    }
}
