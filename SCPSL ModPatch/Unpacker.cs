using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace SCPSL_ModPatch
{
    internal class Unpacker
    {
        readonly string _unpackerPath;
        readonly string _gameAssemblyPath;

        public Unpacker(string unpackerPath, string gameAssemblyPath)
        {
            _unpackerPath = Path.GetFullPath(unpackerPath);
            _gameAssemblyPath = Path.GetFullPath(gameAssemblyPath);
        }

        public async Task UnpackGameAssemblyAsync(IWin32Window owner)
        {
            var downloader = new UnlicenseDownloader();
            string? executablePath = downloader.FindExistingExecutable(_unpackerPath);
            string downloadNotice = executablePath == null
                ? "Unlicense 0.4.0 (x64, 49 MB) will be downloaded from github.com/ergrelet/unlicense and saved beside ModPatch.\r\n\r\n"
                : string.Empty;
            DialogResult userChoice = MessageBox.Show(owner, "GameAssembly.dll appears to be protected by Themida and cannot be loaded yet.\r\n\r\n" +
                downloadNotice + "Try unpacking it with Unlicense? This replaces GameAssembly.dll; keep a separate backup first.",
                "Protected game files", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (userChoice == DialogResult.No)
                return;
            if (executablePath == null)
            {
                using var downloadForm = new DownloadForm(UnlicenseDownloader.Release.DownloadUrl, downloader.ExecutablePath,
                    async (progress, cancellationToken) => executablePath = await downloader.DownloadAsync(progress, cancellationToken));
                if (downloadForm.ShowDialog(owner) != DialogResult.OK)
                    return;
            }
            var unpacker = new Unpacker(executablePath!, _gameAssemblyPath);
            string backupPath = await Task.Run(() => unpacker.UnpackAndReplace());
            MessageBox.Show(owner, $"GameAssembly.dll was unpacked. Press Load game again to continue.\r\n\r\nOriginal DLL backup:\r\n{backupPath}",
                "Unpacking complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// Consumes only this invocation's output, then replaces the DLL with a retained backup.
        /// The process runner is injectable so failure paths can be tested without executing game code.
        /// </summary>
        internal string UnpackAndReplace(Func<ProcessStartInfo, int>? runUnpacker = null)
        {
            string originalSha256 = HashFile(_gameAssemblyPath);
            // Keep staging on the destination volume for File.Replace. Unlicense writes its
            // unpacked_<module> output relative to its working directory, not its executable.
            string workDirectory = Path.Combine(Path.GetDirectoryName(_gameAssemblyPath)!, $".modpatch-unpack-{Guid.NewGuid():N}");
            string outputPath = Path.Combine(workDirectory, "unpacked_GameAssembly.dll");
            string backupPath = _gameAssemblyPath + $".before-unpack-{Guid.NewGuid():N}.bak";
            Directory.CreateDirectory(workDirectory);
            try
            {
                var start = new ProcessStartInfo(_unpackerPath)
                {
                    WorkingDirectory = workDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add(_gameAssemblyPath);
                int exitCode = (runUnpacker ?? RunUnpacker)(start);
                if (exitCode != 0)
                    throw new InvalidOperationException($"Unlicense failed with exit code {exitCode}. The original DLL was not replaced.");
                if (!File.Exists(outputPath))
                    throw new InvalidDataException("Unlicense did not produce a fresh unpacked_GameAssembly.dll. The original DLL was not replaced.");
                ValidateUnpackedAssembly(outputPath);
                if (HashFile(outputPath) == originalSha256)
                    throw new InvalidDataException("Unlicense output is unchanged from the protected DLL. The original DLL was not replaced.");
                if (HashFile(_gameAssemblyPath) != originalSha256)
                    throw new IOException("GameAssembly.dll changed while Unlicense was running. It was not replaced.");

                // Never delete the original first. Replacement also retains it outside the
                // scratch directory, so cleanup cannot remove the recovery copy.
                File.Replace(outputPath, _gameAssemblyPath, backupPath);
                return backupPath;
            }
            finally
            {
                // Cleanup failures must not turn a completed replacement into a reported failure.
                try { Directory.Delete(workDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static int RunUnpacker(ProcessStartInfo start)
        {
            using Process unpacker = Process.Start(start) ?? throw new IOException("Unlicense could not be started.");
            unpacker.WaitForExit();
            return unpacker.ExitCode;
        }

        private static string HashFile(string path)
        {
            using FileStream file = File.OpenRead(path);
            using SHA256 hash = SHA256.Create();
            return Convert.ToHexString(hash.ComputeHash(file));
        }

        private static void ValidateUnpackedAssembly(string path)
        {
            using FileStream file = File.OpenRead(path);
            using var pe = new PEReader(file);
            PEHeaders headers = pe.PEHeaders;
            if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus ||
                (headers.CoffHeader.Characteristics & Characteristics.Dll) == 0 ||
                !headers.SectionHeaders.Any(section => section.SizeOfRawData > 0 &&
                    (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0) ||
                headers.SectionHeaders.Any(section => section.SizeOfRawData < 0 || section.PointerToRawData < 0 ||
                    (long)section.PointerToRawData + section.SizeOfRawData > file.Length))
                throw new InvalidDataException("Unlicense output is not a valid x64 native DLL. The original DLL was not replaced.");
        }
    }
}
