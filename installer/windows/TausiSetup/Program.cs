using WixToolset.BootstrapperApplicationApi;

namespace Tausi.Setup
{
    /// <summary>The installer's screens, started by the bundle (WiX v5: the bootstrapper application is its own program).</summary>
    internal static class Program
    {
        [System.STAThread]
        private static int Main()
        {
            var application = new TausiBootstrapper();
            ManagedBootstrapperApplication.Run(application);
            return application.ExitCode;
        }
    }
}
