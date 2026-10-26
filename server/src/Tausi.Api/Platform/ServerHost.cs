using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.EventLog;
using Tausi.Infrastructure;
using Tausi.Infrastructure.Platform;

namespace Tausi.Api.Platform;

/// <summary>
/// Where an installed server keeps its files. On Windows the installer makes
/// C:\ProgramData\Tausi POS\ with config\, data\, logs\ and backups\; the settings file
/// config\appsettings.Production.json survives updates and holds its secrets encrypted with DPAPI.
/// Linux installs set the folders in /etc/tausi/tausi.env. Development uses the folders next to the project.
/// </summary>
public static class ServerHost
{
    public const string ServiceName = "TausiPOSServer";
    public const string EventLogSource = "Tausi POS";
    public const string SettingsFileName = "appsettings.Production.json";

    /// <summary>C:\ProgramData\Tausi POS on Windows.</summary>
    public static string WindowsHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tausi POS");

    /// <summary>The installed home folder: Tausi:Home when set, else the Windows one when the installer made it.</summary>
    public static string? Home(IConfiguration config)
    {
        var home = config["Tausi:Home"];
        if (!string.IsNullOrWhiteSpace(home)) return home;
        return OperatingSystem.IsWindows() && Directory.Exists(Path.Combine(WindowsHome, "config")) ? WindowsHome : null;
    }

    /// <summary>
    /// The program's own folder when this is an installed server (Tausi__Home set, or the Windows home made
    /// by the installer); null otherwise, so development keeps the project folder.
    /// </summary>
    public static string? InstalledContentRoot()
    {
        var installed = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Tausi__Home"))
                        || (OperatingSystem.IsWindows() && Directory.Exists(Path.Combine(WindowsHome, "config")));
        return installed ? AppContext.BaseDirectory : null;
    }

    public static void Configure(WebApplicationBuilder builder)
    {
        var home = Home(builder.Configuration);
        if (home is not null)
        {
            // After appsettings*.json (so the installed folders win over "data") and before environment
            // variables and the command line (so they can still override).
            var sources = builder.Configuration.Sources;
            var at = sources.Select((s, i) => (s, i)).Where(x => x.s is JsonConfigurationSource).Select(x => x.i + 1).DefaultIfEmpty(0).Max();
            sources.Insert(at, new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    ["Tausi:DataDirectory"] = Path.Combine(home, "data"),
                    ["Tausi:LogDirectory"] = Path.Combine(home, "logs"),
                    ["Tausi:BackupDirectory"] = Path.Combine(home, "backups"),
                    ["Tausi:ConfigDirectory"] = Path.Combine(home, "config"),
                    ["Tausi:SetupTokenFile"] = Path.Combine(home, "setup-token.txt"),
                    ["Tausi:Printing:OutputDirectory"] = Path.Combine(home, "print-output"),
                },
            });
            var config = Path.Combine(home, "config");
            if (Directory.Exists(config))
                sources.Insert(at + 1, new ProtectedJsonSource
                {
                    FileProvider = new PhysicalFileProvider(config),
                    Path = SettingsFileName,
                    Optional = true,
                    ReloadOnChange = false,
                });
        }

        var logs = builder.Configuration["Tausi:LogDirectory"];
        if (!string.IsNullOrWhiteSpace(logs))
            builder.Logging.AddProvider(new FileLoggerProvider(Path.GetFullPath(logs, builder.Environment.ContentRootPath)));

        // Errors also go to the Windows Event Log (Application, source "Tausi POS"), registered by the installer.
        if (OperatingSystem.IsWindows()) UseEventLog(builder);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void UseEventLog(WebApplicationBuilder builder)
    {
        builder.Services.Configure<EventLogSettings>(s =>
        {
            s.SourceName = EventLogSource;
            s.LogName = "Application";
        });
        builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Error);
    }

    /// <summary>The installed settings file, with "dpapi:" values decrypted as they are read.</summary>
    private sealed class ProtectedJsonSource : JsonConfigurationSource
    {
        public override IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            EnsureDefaults(builder);
            return new Provider(this);
        }

        private sealed class Provider(JsonConfigurationSource source) : JsonConfigurationProvider(source)
        {
            public override void Load(Stream stream)
            {
                base.Load(stream);
                foreach (var key in Data.Keys.ToList())
                {
                    var value = Data[key];
                    if (MachineSecrets.IsProtected(value)) Data[key] = MachineSecrets.Unprotect(value!);
                    else if (value is not null && key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
                        Data[key] = MachineSecrets.UnprotectConnectionString(value);
                }
            }
        }
    }

    /// <summary>
    /// Appends a crash (or a failed command's full error) to tausi-crash.log in the installed logs folder,
    /// or the temp folder when there is none: an installer step has no console anyone sees.
    /// </summary>
    public static void WriteCrash(object error)
    {
        try
        {
            var home = OperatingSystem.IsWindows() && Directory.Exists(Path.Combine(WindowsHome, "logs")) ? Path.Combine(WindowsHome, "logs") : Path.GetTempPath();
            File.AppendAllText(Path.Combine(home, "tausi-crash.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write it.
        }
    }

    /// <summary>The folders the data, logs and backups live in, from the settings.</summary>
    public static TausiOptions Options(IConfiguration config) =>
        config.GetSection(TausiOptions.Section).Get<TausiOptions>() ?? new TausiOptions();
}
