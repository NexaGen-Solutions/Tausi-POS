using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Tausi.Application.Backups;
using Tausi.Application.Settings;
using Tausi.Application.Setup;
using Tausi.Application.Updates;
using Tausi.Domain.Common;
using Tausi.Infrastructure;
using Tausi.Infrastructure.Persistence;
using Tausi.Infrastructure.Platform;

namespace Tausi.Api.Platform;

/// <summary>
/// Commands for the installers and NexaGen technicians (run on the server PC, as an administrator):
/// <list type="bullet">
/// <item><c>configure</c>: writes the settings file (secrets encrypted with DPAPI), makes the folders, sets their permissions.</item>
/// <item><c>provision</c>: creates the SQL Server database and gives the service's account its own login as the database owner; registers the update task.</item>
/// <item><c>test-db</c>, <c>wait-health</c>: checks for the installer.</item>
/// <item><c>backup</c>, <c>restore &lt;file&gt;</c>: for upgrades, and when the admin site won't start.</item>
/// <item><c>apply-update</c>: run by the SYSTEM update task.</item>
/// <item><c>reset-admin</c>: a lost admin password (prints a token on this PC).</item>
/// <item><c>business-name</c>, <c>purge</c>, <c>cleanup</c>: for uninstalling.</item>
/// <item><c>enable-https</c>, <c>disable-https</c>: the optional self-signed HTTPS for the admin site.</item>
/// </list>
/// </summary>
public static class InstallCommands
{
    public const string ServiceAccount = @"NT SERVICE\" + ServerHost.ServiceName;

    public static async Task<bool> TryRun(WebApplication app, string[] args)
    {
        try
        {
            Environment.ExitCode = args[0] switch
            {
                "configure" => Configure(app, args),
                "provision" => await Provision(app, args),
                "test-db" => await TestDatabase(app),
                "wait-health" => await WaitHealth(app, args),
                "backup" => await Backup(app, args),
                "restore" => await Restore(app, args),
                "apply-update" => await ApplyUpdate(app),
                "reset-admin" => await ResetAdmin(app),
                "business-name" => await BusinessName(app),
                "purge" => await Purge(app, args),
                "cleanup" => Cleanup(),
                "enable-https" => EnableHttps(app, args),
                "disable-https" => DisableHttps(app),
                _ => -1,
            };
            if (Environment.ExitCode == -1)
            {
                Environment.ExitCode = 0;
                return false;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A technician reads this: the reason, not a stack trace (the log has the details).
            Console.Error.WriteLine(e.GetBaseException() == e ? e.Message : $"{e.Message} ({e.GetBaseException().Message})");
            ServerHost.WriteCrash(e);
            Environment.ExitCode = 1;
        }
        return true;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Flag(string[] args, string name) => args.Contains(name);

    private static string Home(WebApplication app, string[] args) =>
        Option(args, "--home") ?? ServerHost.Home(app.Configuration)
        ?? (OperatingSystem.IsWindows() ? ServerHost.WindowsHome : throw new InvalidOperationException("Give the folder with --home (or set Tausi__Home)."));

    private static SettingsFile Settings(string home) => new(Path.Combine(home, "config", ServerHost.SettingsFileName));

    // ---- configure ----

    /// <summary>
    /// configure --provider SqlServer --sql-server .\TAUSI [--database TausiPOS] [--sql-user U --sql-password P]
    /// configure --provider Postgres --pg-host H [--pg-port 5432] --pg-user U --pg-password P [--database tausipos]
    ///           [--port 5080] [--home DIR] [--force] [--sql-service-account "NT SERVICE\MSSQL$TAUSI"]
    /// An existing settings file is kept (an upgrade) unless --force; the port is updated when given.
    /// </summary>
    private static int Configure(WebApplication app, string[] args)
    {
        var home = Home(app, args);
        foreach (var dir in new[] { "config", "data", "logs", "backups", Path.Combine("backups", "work") })
            Directory.CreateDirectory(Path.Combine(home, dir));

        var file = Settings(home);
        var json = file.Read();
        if (!file.Exists || Flag(args, "--force"))
        {
            var provider = Option(args, "--provider") ?? "SqlServer";
            var connection = provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) ? PostgresConnection(args) : SqlServerConnection(args);
            SettingsFile.Set(json, "ConnectionStrings:Tausi", connection);
            SettingsFile.Set(json, "Database:Provider", provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) ? "Postgres" : "SqlServer");
        }
        var port = Option(args, "--port");
        if (port is not null || !file.Exists)
        {
            port ??= "5080";
            if (!int.TryParse(port, out var p) || p is < 1 or > 65535) throw new InvalidOperationException($"{port} isn't a port number.");
            SetHttpPort(json, p);
        }
        file.Write(json);

        if (OperatingSystem.IsWindows())
        {
            // Only SYSTEM, administrators and the service's own account get into the settings (its
            // secrets), the data (keys, PIN pepper), the logs and the backups. The home folder itself
            // keeps its usual rights, so the person installing can read the setup token written there.
            Lock(Path.Combine(home, "config"), "RX");
            Lock(Path.Combine(home, "data"), "M");
            Lock(Path.Combine(home, "logs"), "M");
            Lock(Path.Combine(home, "backups"), "M");
            Run("icacls.exe", [home, "/grant", $"{ServiceAccount}:(M)", "/Q"]);
            // SQL Server writes and reads its .bak files in backups\work. Its account may be another one;
            // then the backup reads the file through SQL Server instead.
            if (Option(args, "--sql-service-account") is { } sql)
            {
                try
                {
                    Grant(Path.Combine(home, "backups", "work"), sql, "M");
                }
                catch (InvalidOperationException e)
                {
                    Console.WriteLine($"Note: couldn't give {sql} the backups\\work folder ({e.Message}).");
                }
            }
        }
        Console.WriteLine($"Settings: {file.Path}");
        return 0;
    }

    public static void SetHttpPort(JsonObject json, int port)
    {
        SettingsFile.Set(json, "Urls", $"http://0.0.0.0:{port}");
        if (SettingsFile.Get(json, "Kestrel:Endpoints:Http:Url") is not null)
            SettingsFile.Set(json, "Kestrel:Endpoints:Http:Url", $"http://0.0.0.0:{port}");
    }

    private static string SqlServerConnection(string[] args)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = Option(args, "--sql-server") ?? @".\TAUSI",
            InitialCatalog = Option(args, "--database") ?? "TausiPOS",
            TrustServerCertificate = true,
            Encrypt = SqlConnectionEncryptOption.Optional,
        };
        if (Option(args, "--sql-user") is { } user)
        {
            b.UserID = user;
            b.Password = "__PASSWORD__";
            return b.ConnectionString.Replace("__PASSWORD__", MachineSecrets.Protect(Option(args, "--sql-password") ?? ""), StringComparison.Ordinal);
        }
        b.IntegratedSecurity = true;
        return b.ConnectionString;
    }

    private static string PostgresConnection(string[] args)
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = Option(args, "--pg-host") ?? "localhost",
            Port = int.TryParse(Option(args, "--pg-port"), out var port) ? port : 5432,
            Database = Option(args, "--database") ?? "tausipos",
            Username = Option(args, "--pg-user") ?? "tausi",
            Password = "__PASSWORD__",
        };
        return b.ConnectionString.Replace("__PASSWORD__", MachineSecrets.Protect(Option(args, "--pg-password") ?? ""), StringComparison.Ordinal);
    }

    /// <summary>
    /// The folder's rights replaced by: SYSTEM and administrators full control, the service <paramref name="rights"/>.
    /// Set on the folder only (no /T on it): Windows passes these inheritable rights down to everything inside.
    /// (/T would also set (OI)(CI) on files, where those flags are invalid, leaving files nobody can open.)
    /// Anything already inside (an earlier install, or an earlier install's mistakes) is then made to take
    /// the folder's rights again: owned by administrators, its own rights reset to the inherited ones.
    /// </summary>
    private static void Lock(string folder, string rights)
    {
        Run("icacls.exe", [folder, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", $"{ServiceAccount}:(OI)(CI){rights}", "/Q"]);
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) return;
        Run("takeown.exe", ["/F", Path.Combine(folder, "*"), "/A", "/R", "/D", "Y"], check: false);
        Run("icacls.exe", [Path.Combine(folder, "*"), "/reset", "/T", "/C", "/Q"]);
    }

    private static void Grant(string folder, string account, string rights) =>
        Run("icacls.exe", [folder, "/grant", $"{account}:(OI)(CI){rights}", "/Q"]);

    private static (int Code, string Output) Run(string file, IEnumerable<string> args, bool check = true)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} didn't start.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (check && process.ExitCode != 0) throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed: {output.Trim()}");
        return (process.ExitCode, output);
    }

    // ---- provision ----

    /// <summary>
    /// SQL Server with Windows sign-in: creates the database if needed and gives the service's account a
    /// login that owns it (so it can back up and restore it). With a SQL login, creates the database only.
    /// PostgreSQL: the migration creates the database. On Windows it also (re)creates the update task.
    /// </summary>
    private static async Task<int> Provision(WebApplication app, string[] args)
    {
        var provider = app.Configuration.GetSection("Database").Get<DatabaseOptions>()?.Provider ?? DatabaseProvider.Postgres;
        var connection = MachineSecrets.UnprotectConnectionString(app.Configuration.GetConnectionString(TausiOptions.ConnectionStringName) ?? "");
        if (provider == DatabaseProvider.SqlServer)
        {
            var b = new SqlConnectionStringBuilder(connection);
            var database = b.InitialCatalog;
            var windows = b.IntegratedSecurity;
            b.InitialCatalog = "master";
            await using var conn = new SqlConnection(b.ConnectionString);
            await conn.OpenAsync();
            // EXEC can't take a function call: each statement is built in a variable first.
            await Sql(conn, "IF DB_ID(@db) IS NULL BEGIN DECLARE @create nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@db); EXEC(@create); END", database);
            if (windows)
            {
                var account = Option(args, "--service-account") ?? ServiceAccount;
                await Sql(conn, """
                    IF SUSER_ID(@login) IS NULL
                    BEGIN
                        DECLARE @createLogin nvarchar(max) = N'CREATE LOGIN ' + QUOTENAME(@login) + N' FROM WINDOWS';
                        EXEC(@createLogin);
                    END
                    DECLARE @owner sysname = (SELECT SUSER_SNAME(owner_sid) FROM sys.databases WHERE name = @db);
                    IF @owner IS NULL OR @owner <> @login
                    BEGIN
                        DECLARE @sql nvarchar(max) = N'USE ' + QUOTENAME(@db) + N';
                            DECLARE @u sysname = (SELECT name FROM sys.database_principals WHERE sid = SUSER_SID(N''' + REPLACE(@login, '''', '''''') + N'''));
                            IF @u IS NOT NULL AND @u <> N''dbo'' BEGIN DECLARE @drop nvarchar(max) = N''DROP USER '' + QUOTENAME(@u); EXEC(@drop); END
                            ALTER AUTHORIZATION ON DATABASE::' + QUOTENAME(@db) + N' TO ' + QUOTENAME(@login) + N';';
                        EXEC(@sql);
                    END
                    """, database, account);
                Console.WriteLine($"{account} owns the database {database}.");
            }
        }
        if (OperatingSystem.IsWindows())
        {
            // Automatic (delayed start), and restarted after a crash or a stop with an error (MSI can't set these reliably).
            Run("sc.exe", ["config", ServerHost.ServiceName, "start=", "delayed-auto"], check: false);
            Run("sc.exe", ["failureflag", ServerHost.ServiceName, "1"], check: false);
            RegisterUpdateTask();
        }
        return 0;
    }

    private static async Task Sql(SqlConnection conn, string sql, string db, string? login = null)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@db", db);
        if (login is not null) cmd.Parameters.AddWithValue("@login", login);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// "Tausi POS Server Update": runs <c>TausiServer apply-update</c> as SYSTEM, on demand only. The service's
    /// account may start it (its security descriptor), not change it.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterUpdateTask()
    {
        var sid = new System.Security.Principal.NTAccount(ServiceAccount).Translate(typeof(System.Security.Principal.SecurityIdentifier)).Value;
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TausiServer.exe");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>NexaGen Solutions</Author>
                <Description>Installs a checked Tausi POS Server update when the admin site asks for it.</Description>
                <SecurityDescriptor>D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{sid})</SecurityDescriptor>
              </RegistrationInfo>
              <Principals><Principal id="System"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="System"><Exec><Command>"{exe}"</Command><Arguments>apply-update</Arguments><WorkingDirectory>{Path.GetDirectoryName(exe)}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        var path = Path.Combine(Path.GetTempPath(), "tausi-update-task.xml");
        File.WriteAllText(path, xml, System.Text.Encoding.Unicode);
        try
        {
            Run("schtasks.exe", ["/Create", "/TN", UpdateInstaller.TaskName, "/XML", path, "/F"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- checks ----

    private static async Task<int> TestDatabase(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TausiDbContext>();
        if (await db.Database.CanConnectAsync())
        {
            Console.WriteLine("Connected to the database.");
            return 0;
        }
        // CanConnect hides the reason: open it to show it.
        await db.Database.OpenConnectionAsync();
        return 0;
    }

    /// <summary>wait-health [--timeout 120]: waits until this server's /health answers with the database reachable.</summary>
    private static async Task<int> WaitHealth(WebApplication app, string[] args)
    {
        var timeout = TimeSpan.FromSeconds(int.TryParse(Option(args, "--timeout"), out var s) ? s : 120);
        var url = (Option(args, "--url") ?? LocalUrl(app.Configuration)) + "/health";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var until = DateTime.UtcNow + timeout;
        string last = "no answer";
        while (DateTime.UtcNow < until)
        {
            try
            {
                var health = await http.GetFromJsonAsync<JsonObject>(url);
                if (health?["database"]?.GetValue<string>() == "ok")
                {
                    Console.WriteLine($"Tausi POS Server {health["version"]} is running.");
                    return 0;
                }
                last = $"database {health?["database"]}";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                last = e.Message;
            }
            await Task.Delay(2000);
        }
        Console.Error.WriteLine($"The server didn't become healthy within {timeout.TotalSeconds:0} s ({last}). See the logs folder.");
        return 1;
    }

    /// <summary>http://127.0.0.1:&lt;port&gt; from the Urls setting.</summary>
    public static string LocalUrl(IConfiguration config)
    {
        var urls = config["Kestrel:Endpoints:Http:Url"] ?? config["Urls"] ?? "http://0.0.0.0:5080";
        var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? "http://0.0.0.0:5080";
        var port = first.Contains(':', StringComparison.Ordinal) && int.TryParse(first[(first.LastIndexOf(':') + 1)..].TrimEnd('/'), out var p) ? p : 5080;
        return $"http://127.0.0.1:{port}";
    }

    // ---- backups ----

    private static async Task<int> Backup(WebApplication app, string[] args)
    {
        var reason = Option(args, "--reason") == "upgrade" ? BackupReason.Upgrade : BackupReason.Command;
        var record = await app.Services.GetRequiredService<IBackupEngine>().Run(reason, CancellationToken.None);
        var size = record.Size >= 1024 * 1024 ? $"{record.Size / 1024 / 1024.0:0.0} MB" : $"{Math.Max(1, record.Size / 1024)} KB";
        Console.WriteLine($"Backed up and checked: {record.Name} ({size}){(record.CopiedTo is null ? "" : $", copied to {record.CopiedTo}")}");
        if (record.CopyError is not null) Console.WriteLine(record.CopyError);
        return 0;
    }

    /// <summary>
    /// restore &lt;file&gt; [--password P] [--yes] [--no-safety-backup] [--latest-upgrade]: for when the admin site
    /// won't start, a new PC, or an upgrade's rollback (<c>--latest-upgrade</c>: the newest backup taken before an upgrade).
    /// Stop the service first.
    /// </summary>
    private static async Task<int> Restore(WebApplication app, string[] args)
    {
        var engine = app.Services.GetRequiredService<IBackupEngine>();
        string? path;
        if (Flag(args, "--latest-upgrade"))
        {
            var latest = engine.List().Where(b => b.Reason == BackupReason.Upgrade).MaxBy(b => b.CreatedAt);
            path = latest is null ? null : engine.PathOf(latest.Name);
            if (path is null)
            {
                Console.Error.WriteLine("There is no backup from before an upgrade.");
                return 1;
            }
        }
        else
        {
            path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a != Option(args, "--password"));
            if (path is null || !File.Exists(path))
            {
                Console.Error.WriteLine("Usage: TausiServer restore <file.tausibak> [--password P] [--yes]");
                return 2;
            }
        }
        var password = Option(args, "--password");
        var contents = await engine.Inspect(path, password, CancellationToken.None);
        Console.WriteLine($"Backup of {contents.Business}, {contents.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}, Tausi POS {contents.Version}.");
        if (!Flag(args, "--yes"))
        {
            Console.Write($"This replaces everything in this server's database. Type the business name ({contents.Business}) to go on: ");
            if (!BackupAdmin.SameName(Console.ReadLine(), contents.Business))
            {
                Console.Error.WriteLine("Not restored.");
                return 1;
            }
        }
        await DatabaseMigrator.Migrate(app.Services.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None);
        await engine.Restore(path, password, CancellationToken.None, safetyBackup: !Flag(args, "--no-safety-backup"));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TausiDbContext>();
            db.AuditEntries.Add(new Domain.Audit.AuditEntry
            {
                At = DateTimeOffset.UtcNow, Actor = "restore command", Action = Application.Auditing.AuditActions.BackupRestored,
                EntityType = "Backup", EntityId = Path.GetFileName(path),
                NewValueJson = JsonSerializer.Serialize(new { contents.Business, contents.CreatedAt, contents.Version }),
            });
            await db.SaveChangesAsync();
        }
        Console.WriteLine(OperatingSystem.IsWindows()
            ? "Restored. Start the service again: net start TausiPOSServer"
            : "Restored. Start the service again: sudo systemctl start tausi");
        return 0;
    }

    // ---- updates ----

    /// <summary>Run by the SYSTEM update task: checks the pending installer's signature again, then runs it silently.</summary>
    private static async Task<int> ApplyUpdate(WebApplication app)
    {
        var folder = UpdateInstaller.Folder(app.Services.GetRequiredService<IOptions<TausiOptions>>().Value, app.Environment);
        var pendingPath = Path.Combine(folder, UpdateInstaller.PendingFile);
        if (!File.Exists(pendingPath))
        {
            Console.Error.WriteLine("No update is waiting.");
            return 1;
        }
        var pending = JsonSerializer.Deserialize<UpdateInstaller.Pending>(File.ReadAllText(pendingPath))!;
        File.Delete(pendingPath);
        var installer = Path.Combine(folder, Path.GetFileName(pending.File));
        if (!File.Exists(installer)
            || !await ServerUpdates.Verify(installer, pending.Version, pending.Sha256, pending.Signature, app.Environment.IsDevelopment(), CancellationToken.None))
        {
            Console.Error.WriteLine("The waiting update's signature doesn't check out; it was not installed.");
            return 1;
        }
        var logs = app.Configuration["Tausi:LogDirectory"] ?? folder;
        var log = Path.Combine(logs, $"update-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var (code, _) = Run(installer, ["/quiet", "/norestart", "/log", log], check: false);
        try { File.Delete(installer); }
        catch (IOException) { }
        Console.WriteLine(code == 0 ? $"Updated to {pending.Version}." : $"The update failed ({code}); see {log}.");
        return code;
    }

    // ---- admin recovery ----

    private static async Task<int> ResetAdmin(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var token = await scope.ServiceProvider.GetRequiredService<AdminRecovery>().Issue(CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine($"  Reset token:  {token}   (works for one hour)");
        Console.WriteLine($"  Open {LocalUrl(app.Configuration).Replace("127.0.0.1", "this server", StringComparison.Ordinal)}/setup/recover in a browser,");
        Console.WriteLine("  type the token and choose a new admin-site password.");
        Console.WriteLine();
        return 0;
    }

    // ---- uninstalling ----

    private static async Task<int> BusinessName(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        Console.WriteLine((await SettingsRegistry.Load(scope.ServiceProvider.GetRequiredService<TausiDbContext>(), CancellationToken.None))
            .String(SettingsRegistry.BusinessName));
        return 0;
    }

    /// <summary>purge --business "Name" [--home DIR]: deletes the database and the data folder, after the business name is typed.</summary>
    private static async Task<int> Purge(WebApplication app, string[] args)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TausiDbContext>();
        string name;
        try
        {
            name = (await SettingsRegistry.Load(db, CancellationToken.None)).String(SettingsRegistry.BusinessName);
        }
        catch (Exception e) when (e is SqlException or NpgsqlException or InvalidOperationException)
        {
            name = "";
        }
        if (name != "" && !BackupAdmin.SameName(Option(args, "--business"), name))
        {
            Console.Error.WriteLine($"Type the business name exactly ({name}) to delete all data.");
            return 1;
        }
        if (name != "") await db.Database.EnsureDeletedAsync();
        var home = Home(app, args);
        foreach (var dir in new[] { "config", "data", "backups", "logs", "print-output" })
        {
            var path = Path.Combine(home, dir);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        Console.WriteLine("All Tausi POS data on this PC is deleted.");
        return 0;
    }

    private static int Cleanup()
    {
        if (OperatingSystem.IsWindows()) Run("schtasks.exe", ["/Delete", "/TN", UpdateInstaller.TaskName, "/F"], check: false);
        return 0;
    }

    // ---- HTTPS (optional) ----

    /// <summary>
    /// enable-https [--port 5443]: a self-signed certificate made here (this PC's name and addresses), kept in
    /// data\secrets, and an HTTPS address for the admin site next to the HTTP one the terminals use.
    /// </summary>
    private static int EnableHttps(WebApplication app, string[] args)
    {
        var port = int.TryParse(Option(args, "--port"), out var p) ? p : 5443;
        var home = Home(app, args);
        var secrets = Path.Combine(home, "data", "secrets");
        Directory.CreateDirectory(secrets);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Environment.MachineName}, O=Tausi POS", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Environment.MachineName);
        names.AddDnsName("localhost");
        foreach (var address in LanAddresses()) names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pfx = Path.Combine(secrets, "https.pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, password));

        var file = Settings(home);
        var json = file.Read();
        var http = SettingsFile.Get(json, "Urls") ?? "http://0.0.0.0:5080";
        SettingsFile.Set(json, "Kestrel:Endpoints:Http:Url", http.Split(';')[0]);
        SettingsFile.Set(json, "Kestrel:Endpoints:Https:Url", $"https://0.0.0.0:{port}");
        SettingsFile.Set(json, "Kestrel:Endpoints:Https:Certificate:Path", pfx);
        SettingsFile.Set(json, "Kestrel:Endpoints:Https:Certificate:Password", MachineSecrets.Protect(password));
        file.Write(json);
        Console.WriteLine($"HTTPS is on at https://{Environment.MachineName}:{port}/admin after the service restarts.");
        Console.WriteLine($"Certificate fingerprint (SHA-256): {cert.GetCertHashString(HashAlgorithmName.SHA256)}");
        Console.WriteLine("Browsers warn about a self-signed certificate: check the fingerprint matches, then continue.");
        return 0;
    }

    private static int DisableHttps(WebApplication app)
    {
        var file = Settings(Home(app, []));
        var json = file.Read();
        SettingsFile.Set(json, "Kestrel", null);
        file.Write(json);
        Console.WriteLine("HTTPS is off after the service restarts.");
        return 0;
    }

    /// <summary>This PC's IPv4 addresses on its network cards (not loopback).</summary>
    public static IEnumerable<System.Net.IPAddress> LanAddresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
}
