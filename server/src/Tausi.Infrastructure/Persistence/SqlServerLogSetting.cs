using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Tausi.Infrastructure.Persistence;

/// <summary>
/// SQL Server only: a database in the FULL recovery model keeps its log until a log backup clears it, and
/// Tausi POS's own backups are full copies, so the log would grow until the disk fills. One that has never
/// had a log backup moves to SIMPLE; one whose log someone backs up is left alone. Never stops the server.
/// </summary>
public static class SqlServerLogSetting
{
    public static async Task Apply(TausiDbContext db, ILogger log, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return;
        try
        {
            var unmanaged = await db.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*) AS [Value]
                FROM sys.databases d JOIN sys.database_recovery_status s ON s.database_id = d.database_id
                WHERE d.database_id = DB_ID() AND d.recovery_model_desc = 'FULL' AND s.last_log_backup_lsn IS NULL
                """).SingleAsync(ct);
            if (unmanaged == 0) return;
            await db.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET RECOVERY SIMPLE", ct);
            log.LogInformation("The database now uses the SIMPLE recovery model: nothing was backing up its log");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("Couldn't check the database's recovery model: {Message}", e.Message);
        }
    }
}
