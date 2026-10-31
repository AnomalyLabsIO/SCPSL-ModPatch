namespace SCPSL_ModPatch
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Handle command line arguments before initializing Windows Forms.
            if (args.Length > 0)
                return CommandLine.HeadlessCommand.Run(args);

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }
    }
}
