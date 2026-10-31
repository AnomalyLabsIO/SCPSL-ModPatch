using System.Diagnostics;

namespace SCPSL_ModPatch
{
    internal class FASM
    {
        const string FASM_FILENAME = @".\fasm_binaries\FASM.exe";

        private static string GetFasmPath(bool interactive)
        {
            if (interactive)
                return FASM_FILENAME;
            return Path.Combine(AppContext.BaseDirectory, "fasm_binaries", "FASM.exe");
        }

        public static bool IsFasmExisting(bool interactive = true)
        {
            return File.Exists(GetFasmPath(interactive));
        }

        public static string AssembleToHex(string mnemonics, bool interactive = true)
        {
            string fasmPath = GetFasmPath(interactive);
            if (!File.Exists(fasmPath))
            {
                throw new FasmAssembleException("FASM Assembler binary was not found.");
            }
            using Process fasmProcess = new Process();
            fasmProcess.StartInfo = new ProcessStartInfo()
            {
                FileName = fasmPath,
                Arguments = mnemonics,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = !interactive,
                CreateNoWindow = true
            };
            fasmProcess.Start();
            if (!interactive)
                fasmProcess.StandardInput.Close();

            // Read both streams together to avoid blocking on a full output pipe.
            Task<string> errorMessageTask = fasmProcess.StandardError.ReadToEndAsync();
            Task<string> hexInstructionsTask = fasmProcess.StandardOutput.ReadToEndAsync();
            Task completionTask = Task.WhenAll(fasmProcess.WaitForExitAsync(), errorMessageTask, hexInstructionsTask);
            try
            {
                if (interactive)
                    completionTask.GetAwaiter().GetResult();
                else
                    completionTask.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                try
                {
                    fasmProcess.Kill(entireProcessTree: true);
                    fasmProcess.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // The process already exited.
                }
                throw new FasmAssembleException("FASM did not complete within 30 seconds.");
            }

            string errorMessage = errorMessageTask.GetAwaiter().GetResult();
            string hexInstructions = hexInstructionsTask.GetAwaiter().GetResult();

            if (!string.IsNullOrEmpty(errorMessage) || string.IsNullOrEmpty(hexInstructions))
            {
                throw new FasmAssembleException("Failed to assemble mnemonics.");
            }

            return hexInstructions;
        }
    }
}
