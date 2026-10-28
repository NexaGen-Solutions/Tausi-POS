using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Backups;
using Tausi.Application.Security;
using Tausi.Domain.People;
using Tausi.Domain.Printing;

namespace Tausi.Application.Support;

public sealed record DiagnosticTerminal(string Name, bool Active, bool Paired, string? AppVersion, DateTimeOffset? LastSeenAt);
public sealed record DiagnosticPrinter(string Name, string Connection, int PaperWidthMm, string Status, string? LastError);
public sealed record DiagnosticPrintJob(DateTimeOffset CreatedAt, string Printer, string Kind, int Attempts, string? LastError);

/// <summary>What NexaGen support needs to see, with no secrets, PINs or customers' sales in it.</summary>
public sealed record DiagnosticsSummary(
    DateTimeOffset CollectedAt,
    string Business,
    IReadOnlyList<DiagnosticTerminal> Terminals,
    IReadOnlyList<DiagnosticPrinter> Printers,
    IReadOnlyList<DiagnosticPrintJob> FailedPrintJobs,
    BackupHealth Backup,
    IReadOnlyList<BackupRecord> Backups,
    int OpenTickets,
    DateTimeOffset? DayOpenedAt);

/// <summary>
/// "Download diagnostics" (Settings → System, settings.manage): the server's state for NexaGen support.
/// The API adds the health check and the recent log files and hands it over as a zip; the owner sends
/// it on, so it needs no internet. Audited.
/// </summary>
public sealed class Diagnostics(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IBackupEngine backups, IAuditLog audit, TimeProvider clock)
{
    /// <summary>Failed print jobs and backups: the most recent this many.</summary>
    public const int Recent = 50;

    public async Task<DiagnosticsSummary> Collect(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, null, ct);
        var now = clock.GetUtcNow();
        var business = await db.BusinessSettings.AsNoTracking().Select(b => b.Name).FirstOrDefaultAsync(ct) ?? "";
        var terminals = await db.Terminals.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new DiagnosticTerminal(t.Name, t.Active, t.DeviceTokenHash != null, t.AppVersion, t.LastSeenAt))
            .ToListAsync(ct);
        var printers = (await db.Printers.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct))
            .Select(p => new DiagnosticPrinter(p.Name, p.ConnectionMode.ToString(), p.PaperWidthMm, p.Status.ToString(), p.LastError))
            .ToList();
        var failed = (await db.PrintJobs.AsNoTracking().Include(j => j.Printer)
                .Where(j => j.Status == PrintJobStatus.Failed)
                .OrderByDescending(j => j.CreatedAt).Take(Recent).ToListAsync(ct))
            .Select(j => new DiagnosticPrintJob(j.CreatedAt, j.Printer?.Name ?? "", j.Kind.ToString(), j.Attempts, j.LastError))
            .ToList();
        var openTickets = await db.Tickets.CountAsync(t => t.Status == Domain.Service.TicketStatus.Open, ct);
        var day = await db.WorkPeriods.AsNoTracking().Where(w => w.IsOpen).Select(w => (DateTimeOffset?)w.OpenedAt).FirstOrDefaultAsync(ct);

        audit.Write(new AuditRecord(AuditActions.DiagnosticsDownloaded, "System", null));
        await db.SaveChangesAsync(ct);
        return new DiagnosticsSummary(now, business, terminals, printers, failed, backups.Health(now),
            backups.List().Take(Recent).ToList(), openTickets, day);
    }
}
