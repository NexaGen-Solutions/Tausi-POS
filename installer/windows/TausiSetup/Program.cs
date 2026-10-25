using WixToolset.BootstrapperApplicationApi;

namespace Tausi.Setup
{
    /// <summary>The installer's screens, started by the bundle (WiX v5: the bootstrapper application is its own program).</summary>
    internal static class Program
    {
        [System.STAThread]
        private static int Main()
        {
            try
            {
                var application = new TausiBootstrapper();
                ManagedBootstrapperApplication.Run(application);
                return application.ExitCode;
            }
            catch (System.Exception e)
            {
                // Before it reaches the engine there is no log: leave the reason where support finds it.
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TausiSetup-error.txt"), e.ToString());
                return 1;
            }
        }
    }
}
