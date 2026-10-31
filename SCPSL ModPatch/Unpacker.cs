using System.Diagnostics;

namespace SCPSL_ModPatch
{
    internal class Unpacker
    {
        string _unpackerPath;
        string _gameAssemblyPath;

        public Unpacker(string unpackerPath, string gameAssemblyPath)
        {
            _unpackerPath = unpackerPath;
            _gameAssemblyPath = gameAssemblyPath;
        }

        public void UnpackGameAssembly()
        {
            DialogResult userChoice = MessageBox.Show("GameAssembly.dll appears to be protected by Themida and cannot be loaded yet.\r\n\r\n" +
                "Try unpacking it with Unlicense? This replaces GameAssembly.dll; keep a separate backup first.",
                "Protected game files", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (userChoice == DialogResult.No)
                return;
            if (!File.Exists(_unpackerPath))
            {
                MessageBox.Show("Unlicense was not found. In Open settings, select the Unlicense executable, then press Load game again.",
                    "Unpacker not found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            StartThemidaUnpackerProcess();

            string unpackerFolder = Path.GetDirectoryName(_unpackerPath) ?? string.Empty;
            string unpackedGameAssemblyPath = @$"{unpackerFolder}\unpacked_GameAssembly.dll";
            if (!File.Exists(unpackedGameAssemblyPath))
            {
                MessageBox.Show("Unlicense did not produce unpacked_GameAssembly.dll. Check its output before trying again.",
                    "Unpacking incomplete", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            File.Delete(_gameAssemblyPath);
            File.Move(unpackedGameAssemblyPath, _gameAssemblyPath);

            MessageBox.Show("GameAssembly.dll was unpacked. Press Load game again to continue.", "Unpacking complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void StartThemidaUnpackerProcess()
        {
            Process unpacker = new Process();
            unpacker.StartInfo.FileName = _unpackerPath;
            unpacker.StartInfo.Arguments = $"\"{_gameAssemblyPath}\"";
            unpacker.Start();
            unpacker.WaitForExit();
            unpacker.Close();
        }
    }
}
