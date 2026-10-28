using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tausi.Application.Backups;
using Tausi.Application.Setup;
using Tausi.Application.Updates;
using Tausi.Infrastructure;
using Tausi.Infrastructure.Persistence;

namespace Tausi.Api.Endpoints;

public static class SystemEndpoints
{
    public sealed record PrinterHealth(Guid Id, string Name, string Status, string? LastError);

    /// <summary>
    /// /health (and /api/health): the version, the database, free disk space where the data lives, the
    /// last backup and the licence state. "degraded" when the database is unreachable, the disk is
    /// nearly full or the backups need attention.
    /// </summary>
    public sealed record Health(
        string Status,
        string Version,
        string Server,
        string Database,
        long? DiskFreeBytes,
        DateTimeOffset? LastBackupAt,
        string Backup,
        string? BackupMessage,
        string? Licence,
        string MinimumTerminalVersion,
        IReadOnlyList<PrinterHealth> Printers);

    /// <summary>Below this much free space the server reports itself degraded.</summary>
    public const long LowDisk = 1024L * 1024 * 1024;

    public static readonly string Version =
        typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", Get).WithTags("System").RequireCors(CorsSetup.TerminalApp);
        app.MapGet("/health", Get).WithTags("System");

        // Settings → System → Check the data: the books, settled tickets and stock agree (settings.manage).
        app.MapGet("/api/admin/system/check", (Tausi.Application.Support.ConsistencyCheck check, CancellationToken ct) => check.RunAsAdmin(ct))
            .WithTags("System").RequireAuthorization();

        // Settings → System → Download diagnostics: a zip for NexaGen support (settings.manage, audited).
        app.MapGet("/api/admin/system/diagnostics", async (
            Tausi.Application.Support.Diagnostics diagnostics, TausiDbContext db, IBackupEngine backups, LicenceService licences,
            IOptions<TausiOptions> options, IConfiguration config, IHostEnvironment env, TimeProvider clock, CancellationToken ct) =>
        {
            var summary = await diagnostics.Collect(ct);
            var health = await Get(db, backups, licences, options, env, clock, ct);
            var logs = config["Tausi:LogDirectory"] is { Length: > 0 } folder ? Path.GetFullPath(folder, env.ContentRootPath) : null;
            var zip = DiagnosticsZip.Build(health, summary, logs, clock.GetLocalNow(), config["Database:Provider"] ?? "");
            return Results.File(zip, "application/zip", $"tausi-diagnostics-{clock.GetLocalNow():yyyyMMdd-HHmm}.zip");
        }).WithTags("System").RequireAuthorization();
    }

    private static async Task<Health> Get(
        TausiDbContext db, IBackupEngine backups, LicenceService licences, IOptions<TausiOptions> options, IHostEnvironment env,
        TimeProvider clock, CancellationToken ct)
    {
        IReadOnlyList<PrinterHealth> printers = [];
        var database = "ok";
        string? licence = null;
        try
        {
            printers = await db.Printers.AsNoTracking().OrderBy(p => p.Name)
                .Select(p => new PrinterHealth(p.Id, p.Name, p.Status.ToString(), p.LastError))
                .ToListAsync(ct);
            licence = (await licences.Status(ct)).Status;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            database = "unreachable";
        }

        var backup = backups.Health(clock.GetUtcNow());
        var disk = DiskFree(Path.GetFullPath(options.Value.DataDirectory, env.ContentRootPath));
        var ok = database == "ok" && !backup.NeedsAttention && (disk is null or >= LowDisk);
        return new Health(ok ? "ok" : "degraded", Version, Environment.MachineName, database, disk, backup.LastSuccessAt,
            backup.State, backup.Message, licence, TerminalVersions.Minimum, printers);
    }

    private static long? DiskFree(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (OperatingSystem.IsLinux())
            {
                // The drive the folder is on: the deepest mount point containing it.
                var drive = DriveInfo.GetDrives().Where(d => d.IsReady && Path.GetFullPath(folder).StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                    .MaxBy(d => d.RootDirectory.FullName.Length);
                return drive?.AvailableFreeSpace;
            }
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
