using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Day;

public sealed record WorkPeriodDto(Guid Id, DateTimeOffset OpenedAt, Guid OpenedByUserId);

/// <summary>
/// Opens the business day (a work period). Needs <c>day.open_close</c>. Only one day can be open;
/// a unique index makes sure of it even if two terminals try at once.
/// </summary>
public sealed class OpenDay(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public async Task<WorkPeriodDto> Execute(CancellationToken ct)
    {
        var user = session.RequireUser();
        var grant = await permissions.Require(user, PermissionKeys.DayOpenClose, approval.ApprovalToken, ct);

        if (await db.WorkPeriods.AnyAsync(w => w.IsOpen, ct))
            throw new ConflictException("The day is already open.");

        var period = new WorkPeriod
        {
            OpenedAt = clock.GetUtcNow(),
            OpenedByUserId = user.UserId,
            OpenedTerminalId = user.TerminalId ?? session.Terminal?.TerminalId,
        };
        db.WorkPeriods.Add(period);
        audit.Write(new AuditRecord(
            AuditActions.DayOpened, nameof(WorkPeriod), period.Id.ToString(),
            NewValue: new { period.OpenedAt }, ApprovedByUserId: grant.ApprovedByUserId));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            throw new ConflictException("The day is already open.");
        }

        await events.Publish(PosEventNames.DayOpened, new { workPeriodId = period.Id, openedAt = period.OpenedAt }, ct);
        return new WorkPeriodDto(period.Id, period.OpenedAt, period.OpenedByUserId);
    }
}

public static class WorkPeriods
{
    public static Task<WorkPeriodDto?> Current(ITausiDb db, CancellationToken ct) =>
        db.WorkPeriods.AsNoTracking()
            .Where(w => w.IsOpen)
            .Select(w => new WorkPeriodDto(w.Id, w.OpenedAt, w.OpenedByUserId))
            .FirstOrDefaultAsync(ct);
}

/// <summary>
/// A day closes only when no ticket is open: the cashier settles, moves or merges everything first,
/// so every sale belongs to exactly one day (as in SambaPOS). Cash-up uses this when it closes days.
/// </summary>
public static class DayClosing
{
    public const string OpenTicketsCode = "open_tickets";

    public static async Task EnsureNoOpenTickets(ITausiDb db, CancellationToken ct)
    {
        var open = await db.Tickets.AsNoTracking()
            .Where(k => k.Status == Domain.Service.TicketStatus.Open)
            .OrderBy(k => k.Number).Select(k => k.Number).ToListAsync(ct);
        if (open.Count == 0) return;
        var list = string.Join(", ", open.Take(10).Select(n => $"#{n}")) + (open.Count > 10 ? ", …" : "");
        throw new ConflictException(
            $"The day can't close while {open.Count} ticket{(open.Count == 1 ? " is" : "s are")} open ({list}). Settle or move them first.",
            OpenTicketsCode);
    }
}
