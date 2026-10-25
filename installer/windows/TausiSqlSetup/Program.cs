using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;

namespace Tausi.SqlSetup
{
    /// <summary>
    /// Installs SQL Server 2022 Express as a named instance for Tausi POS:
    ///   TausiSqlSetup.exe --instance TAUSI [--media C:\path\SQLEXPR_x64_ENU.exe]
    /// Without --media it downloads Microsoft's Express installer (the "SSEI" bootstrapper), lets it fetch the
    /// Core package, then installs silently: Windows sign-in, administrators and SYSTEM as SQL administrators
    /// (the installer creates the database as SYSTEM), TCP on. The Windows Firewall stays closed to it, so only
    /// this PC uses it. Exit code: 0 (or 3010, restart needed) on success.
    /// </summary>
    internal static class Program
    {
        private const string Bootstrapper = "https://go.microsoft.com/fwlink/p/?linkid=2216019&clcid=0x409&culture=en-us&country=us";

        private static int Main(string[] args)
        {
            var instance = Option(args, "--instance") ?? "TAUSI";
            var media = Option(args, "--media");
            var work = Path.Combine(Path.GetTempPath(), "TausiSqlSetup");
            Directory.CreateDirectory(work);
            try
            {
                if (string.IsNullOrWhiteSpace(media)) media = Download(work);
                if (!File.Exists(media))
                {
                    Console.Error.WriteLine($"The SQL Server Express installer isn't at {media}.");
                    return 2;
                }
                // The full package extracts itself and runs setup with these arguments.
                var code = Run(media,
                    "/QS /ACTION=Install /FEATURES=SQLEngine /INSTANCENAME=" + instance +
                    " /SQLSVCACCOUNT=\"NT Service\\MSSQL$" + instance + "\"" +
                    " /SQLSYSADMINACCOUNTS=\"BUILTIN\\Administrators\" \"NT AUTHORITY\\SYSTEM\"" +
                    " /TCPENABLED=1 /NPENABLED=0 /BROWSERSVCSTARTUPTYPE=Disabled /UPDATEENABLED=0" +
                    " /IACCEPTSQLSERVERLICENSETERMS /SUPPRESSPRIVACYSTATEMENTNOTICE");
                Console.WriteLine($"SQL Server Express setup finished with {code}.");
                return code;
            }
            catch (Exception e) when (e is WebException || e is IOException || e is InvalidOperationException)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }

        /// <summary>Microsoft's bootstrapper, then its Core package (SQLEXPR_x64_ENU.exe) into the work folder.</summary>
        private static string Download(string work)
        {
            var ssei = Path.Combine(work, "SQL2022-SSEI-Expr.exe");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            using (var client = new WebClient()) client.DownloadFile(Bootstrapper, ssei);
            var mediaFolder = Path.Combine(work, "media");
            Directory.CreateDirectory(mediaFolder);
            var code = Run(ssei, $"/ACTION=Download /MEDIAPATH=\"{mediaFolder}\" /MEDIATYPE=Core /QUIET");
            if (code != 0) throw new InvalidOperationException($"Downloading SQL Server Express failed ({code}).");
            return Directory.GetFiles(mediaFolder, "SQLEXPR*.exe", SearchOption.AllDirectories).FirstOrDefault()
                   ?? throw new InvalidOperationException("The SQL Server Express download has no installer in it.");
        }

        private static int Run(string file, string arguments)
        {
            using (var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false }))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static string Option(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
