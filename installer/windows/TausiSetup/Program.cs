using System;
using System.IO;
using System.Runtime.CompilerServices;
using WixToolset.BootstrapperApplicationApi;

namespace Tausi.Setup
{
    /// <summary>The installer's screens, started by the bundle (WiX v5: the bootstrapper application is its own program).</summary>
    internal static class Program
    {
        private static readonly string ErrorFile = Path.Combine(Path.GetTempPath(), "TausiSetup-error.txt");

        [STAThread]
        private static int Main()
        {
            // Before it reaches the engine there is no log: leave the reason where support finds it.
            AppDomain.CurrentDomain.UnhandledException += (s, e) => File.AppendAllText(ErrorFile, "Unhandled: " + e.ExceptionObject + Environment.NewLine);
            try
            {
                return Start();
            }
            catch (Exception e)
            {
                File.AppendAllText(ErrorFile, e + Environment.NewLine);
                return 1;
            }
        }

        // Separate, so a missing assembly fails here, inside Main's try, not while Main is compiled.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Start()
        {
            var application = new TausiBootstrapper();
            ManagedBootstrapperApplication.Run(application);
            return application.ExitCode;
        }
    }
}
