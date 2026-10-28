using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.People;

namespace Tausi.Application.Settings;

public sealed record TrainingTerminalDto(Guid Id, string Name, bool Training, long RowVersion);
public sealed record TrainingStatusDto(IReadOnlyList<TrainingTerminalDto> Terminals, int Tickets, int OpenTickets);
public sealed record SetTrainingRequest(bool Training, long RowVersion);
public sealed record TrainingClearedDto(int Tickets);

/// <summary>
/// Settings → Training mode (settings.manage). Training is switched on per terminal: its tickets,
/// payments, shifts and logs are practice (see ITrainingData), kept out of reports, the ledger, stock
/// and cash-ups, and its slips say TRAINING. Clear training data deletes them all. Both are audited.
/// </summary>
public sealed class TrainingAdmin(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IAuditLog audit, IPosEvents events)
{
    private Task Require(CancellationToken ct) => permissions.Require(session.RequireUser(), PermissionKeys.SettingsManage, null, ct);

    public async Task<TrainingStatusDto> Status(CancellationToken ct)
    {
        await Require(ct);
        var terminals = await db.Terminals.AsNoTracking().Where(t => t.RevokedAt == null).OrderBy(t => t.Name)
            .Select(t => new TrainingTerminalDto(t.Id, t.Name, t.TrainingMode, t.RowVersion)).ToListAsync(ct);
        var tickets = db.Tickets.IgnoreQueryFilters().Where(t => t.IsTraining);
        return new TrainingStatusDto(terminals, await tickets.CountAsync(ct),
            await tickets.CountAsync(t => t.Status == Domain.Service.TicketStatus.Open, ct));
    }

    public async Task<TrainingTerminalDto> SetTerminal(Guid terminalId, SetTrainingRequest request, CancellationToken ct)
    {
        await Require(ct);
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == terminalId && t.RevokedAt == null, ct)
            ?? throw new NotFoundException("Terminal not found.");
        if (terminal.RowVersion != request.RowVersion)
            throw new ConflictException("This terminal was changed by someone else. Reload and try again.", "stale");
        if (terminal.TrainingMode != request.Training)
        {
            audit.Write(new AuditRecord(AuditActions.TrainingModeChanged, "Terminal", terminal.Id.ToString(),
                OldValue: new { training = terminal.TrainingMode }, NewValue: new { training = request.Training }));
            terminal.TrainingMode = request.Training;
            await db.SaveChangesAsync(ct);
            // The terminal switches its screens (the orange band) and its table map at once.
            await events.Publish(PosEventNames.SettingsChanged, new { training = terminal.Id }, ct);
            await events.Publish(PosEventNames.TablesChanged, new { }, ct);
        }
        return new TrainingTerminalDto(terminal.Id, terminal.Name, terminal.TrainingMode, terminal.RowVersion);
    }

    /// <summary>Deletes every training ticket, line, payment, log, shift, day and their print jobs.</summary>
    public async Task<TrainingClearedDto> Clear(CancellationToken ct)
    {
        await Require(ct);
        var tickets = db.Tickets.IgnoreQueryFilters().Where(t => t.IsTraining);
        var count = await tickets.CountAsync(ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.PrintJobs.Where(j => j.TicketId != null && tickets.Select(t => t.Id).Contains(j.TicketId.Value)).ExecuteDeleteAsync(ct);
            await db.VoidLogs.IgnoreQueryFilters().Where(x => x.IsTraining).ExecuteDeleteAsync(ct);
            await db.GiftLogs.IgnoreQueryFilters().Where(x => x.IsTraining).ExecuteDeleteAsync(ct);
            await db.DiscountLogs.IgnoreQueryFilters().Where(x => x.IsTraining).ExecuteDeleteAsync(ct);
            await db.ApprovalRequests.IgnoreQueryFilters().Where(x => x.IsTraining).ExecuteDeleteAsync(ct);
            await db.Payments.IgnoreQueryFilters().Where(x => x.IsTraining).ExecuteDeleteAsync(ct);
            await db.OrderLines.IgnoreQueryFilters().Where(x => x.IsTraining).ExecuteDeleteAsync(ct);
            await tickets.Where(t => t.MergedIntoTicketId != null).ExecuteUpdateAsync(u => u.SetProperty(t => t.MergedIntoTicketId, (Guid?)null), ct);
            await tickets.ExecuteDeleteAsync(ct);
            await db.DrawerMovements.IgnoreQueryFilters().Where(x => x.IsTraining).ExecuteDeleteAsync(ct);
            await db.CashierShifts.IgnoreQueryFilters().Where(x => x.IsTraining).ExecuteDeleteAsync(ct);
            await db.WorkPeriods.IgnoreQueryFilters().Where(x => x.IsTraining).ExecuteDeleteAsync(ct);
            await db.AuditEntries.IgnoreQueryFilters().Where(x => x.IsTraining).ExecuteDeleteAsync(ct);
            audit.Write(new AuditRecord(AuditActions.TrainingDataCleared, "Training", null, NewValue: new { tickets = count }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        await events.Publish(PosEventNames.TablesChanged, new { }, ct);
        return new TrainingClearedDto(count);
    }
}
