using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Tausi.Setup
{
    /// <summary>What the screens check before installing: the database connection, a free port, this PC's addresses.</summary>
    public static class Checks
    {
        public const string Home = @"C:\ProgramData\Tausi POS";

        /// <summary>Connects to SQL Server and says whether this Windows user (or the SQL login) can create the database.</summary>
        public static string TestSqlServer(string instance, string user, string password, out bool ok)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = instance,
                InitialCatalog = "master",
                ConnectTimeout = 10,
            };
            if (string.IsNullOrEmpty(user)) builder.IntegratedSecurity = true;
            else
            {
                builder.UserID = user;
                builder.Password = password;
            }
            try
            {
                using (var conn = new SqlConnection(builder.ConnectionString))
                {
                    conn.Open();
                    using (var cmd = new SqlCommand(
                        "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)), CAST(SERVERPROPERTY('Edition') AS nvarchar(100)), " +
                        "IS_SRVROLEMEMBER('sysadmin') + IS_SRVROLEMEMBER('dbcreator')", conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        reader.Read();
                        var version = reader.GetString(0);
                        var edition = reader.GetString(1);
                        var canCreate = !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2)) > 0;
                        ok = true;
                        return $"Connected: SQL Server {version} ({edition})." + (canCreate
                            ? " Tausi POS will use its own database, TausiPOS, and never touches the others."
                            : " Note: this login can't create databases; ask for TausiPOS to be created first.");
                    }
                }
            }
            catch (SqlException e)
            {
                ok = false;
                return "Couldn't connect: " + e.Message;
            }
        }

        /// <summary>PostgreSQL: whether something answers at the address (the password is checked while installing).</summary>
        public static string TestPostgres(string host, int port, out bool ok)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.BeginConnect(host, port, null, null);
                    ok = connect.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)) && client.Connected;
                    return ok ? $"PostgreSQL answers at {host}:{port}. The user name and password are checked while installing."
                              : $"Nothing answers at {host}:{port}.";
                }
            }
            catch (SocketException e)
            {
                ok = false;
                return $"Nothing answers at {host}:{port}: {e.Message}";
            }
        }

        /// <summary>Whether nothing on this PC listens on the port yet (or it's the installed server's own).</summary>
        public static bool PortFree(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        /// <summary>This PC's IPv4 addresses on its network cards.</summary>
        public static List<string> Addresses() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();

        /// <summary>The setup token the server wrote on its first start (empty when setup is finished or a backup was restored).</summary>
        public static string SetupToken()
        {
            var path = Path.Combine(Home, "setup-token.txt");
            try
            {
                return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "";
            }
        }

        /// <summary>"NT SERVICE\MSSQL$SQLEXPRESS" for .\SQLEXPRESS, "NT SERVICE\MSSQLSERVER" for the default instance.</summary>
        public static string SqlServiceAccount(string instance)
        {
            var slash = instance.IndexOf('\\');
            var name = slash >= 0 ? instance.Substring(slash + 1) : "";
            return string.IsNullOrEmpty(name) || name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                ? @"NT SERVICE\MSSQLSERVER"
                : @"NT SERVICE\MSSQL$" + name.ToUpperInvariant();
        }

        /// <summary>Whether Microsoft's download server answers (SQL Server Express is downloaded during the install).</summary>
        public static bool Online()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("https://go.microsoft.com/fwlink/p/?linkid=2216019");
                request.Method = "HEAD";
                request.Timeout = 8000;
                using (request.GetResponse()) return true;
            }
            catch (WebException)
            {
                return false;
            }
        }
    }
}
