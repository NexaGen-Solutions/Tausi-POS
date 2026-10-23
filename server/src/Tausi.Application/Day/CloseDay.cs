using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Approvals;
using Tausi.Application.Auditing;
using Tausi.Application.Cash;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.Cash;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Day;

/// <summary>A ticket that keeps the day open: "#1042 · Table 7 · edna · 1,370" (part paid ones too).</summary>
public sealed record OpenTicketDto(Guid Id, long Number, string Where, string Waiter, decimal Total, decimal Paid);

/// <summary>A cashier shift that keeps the day open.</summary>
public sealed record OpenShiftDto(Guid Id, string Cashier, string Terminal, DateTimeOffset OpenedAt);

/// <summary>A request still waiting; it doesn't block the close and expires with it.</summary>
public sealed record PendingRequestSummary(Guid Id, string Summary, string RequestedBy);

public sealed record DayBlockersDto(
    IReadOnlyList<OpenTicketDto> OpenTickets, IReadOnlyList<OpenShiftDto> OpenShifts, IReadOnlyList<PendingRequestSummary> PendingRequests)
{
    public bool Blocked => OpenTickets.Count > 0 || OpenShifts.Count > 0;
}

/// <summary>
/// The day can't close: the open tickets and open shifts are listed (the API returns them as
/// <c>openTickets</c> and <c>openShifts</c>).
/// </summary>
public sealed class OpenTicketsException(string message, IReadOnlyList<OpenTicketDto> tickets, IReadOnlyList<OpenShiftDto>? shifts = null)
    : ConflictException(message, DayClosing.OpenTicketsCode)
{
    public IReadOnlyList<OpenTicketDto> Tickets { get; } = tickets;
    public IReadOnlyList<OpenShiftDto> Shifts { get; } = shifts ?? [];
}

/// <summary>
/// The owner's rule: a day (work period) closes only when no ticket is open, part-paid ones
/// included, and no cashier shift is open, so every sale and every drawer belongs to exactly one
/// day. Tickets emptied by voids are closed with <see cref="CloseEmptyTicket"/>.
/// </summary>
public static class DayClosing
{
    public const string OpenTicketsCode = "open_tickets";

    public static async Task<IReadOnlyList<OpenTicketDto>> OpenTickets(ITausiDb db, CancellationToken ct)
    {
        var open = await db.Tickets.AsNoTracking().Include(k => k.Table).Include(k => k.Lines)
            .Where(k => k.Status == TicketStatus.Open).ToListAsync(ct);
        var ids = open.Select(k => k.Id).ToList();
        var userIds = open.Select(k => k.OpenedByUserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var paid = (await db.Payments.AsNoTracking().Where(p => ids.Contains(p.TicketId) && p.Status == PaymentStatus.Pending)
                .Select(p => new { p.TicketId, p.Amount }).ToListAsync(ct))
            .GroupBy(p => p.TicketId).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        return open.OrderBy(k => k.Number)
            .Select(k => new OpenTicketDto(k.Id, k.Number, TicketPrints.Where(k), users.GetValueOrDefault(k.OpenedByUserId, "?"),
                k.Total, paid.GetValueOrDefault(k.Id)))
            .ToList();
    }

    public static async Task<IReadOnlyList<OpenShiftDto>> OpenShifts(ITausiDb db, CancellationToken ct)
    {
        var rows = await db.CashierShifts.AsNoTracking().Where(s => s.Status == ShiftStatus.Open)
            .Select(s => new
            {
                s.Id,
                Cashier = db.Users.Where(u => u.Id == s.CashierUserId).Select(u => u.Name).FirstOrDefault(),
                Terminal = db.Terminals.Where(t => t.Id == s.TerminalId).Select(t => t.Name).FirstOrDefault(),
                s.OpenedAt,
            }).ToListAsync(ct);
        return rows.OrderBy(s => s.OpenedAt).Select(s => new OpenShiftDto(s.Id, s.Cashier ?? "?", s.Terminal ?? "?", s.OpenedAt)).ToList();
    }

    public static async Task<DayBlockersDto> Blockers(ITausiDb db, CancellationToken ct)
    {
        var pending = await db.ApprovalRequests.AsNoTracking().Where(r => r.Status == ApprovalStatus.Pending)
            .Select(r => new PendingRequestSummary(r.Id, r.Summary,
                db.Users.Where(u => u.Id == r.RequestedByUserId).Select(u => u.Name).FirstOrDefault() ?? "?"))
            .ToListAsync(ct);
        return new DayBlockersDto(await OpenTickets(db, ct), await OpenShifts(db, ct), pending);
    }

    public static async Task EnsureNoOpenTickets(ITausiDb db, CancellationToken ct)
    {
        var tickets = await OpenTickets(db, ct);
        var shifts = await OpenShifts(db, ct);
        if (tickets.Count == 0 && shifts.Count == 0) return;
        var parts = new List<string>();
        if (tickets.Count > 0)
            parts.Add($"{tickets.Count} ticket{(tickets.Count == 1 ? " is" : "s are")} open: "
                + string.Join("; ", tickets.Take(10).Select(k =>
                    $"#{k.Number} {k.Where} · {k.Waiter} · {k.Total:#,##0.##}" + (k.Paid > 0 ? " (part paid)" : "")))
                + (tickets.Count > 10 ? "; …" : ""));
        if (shifts.Count > 0)
            parts.Add($"{shifts.Count} cashier shift{(shifts.Count == 1 ? " is" : "s are")} open: "
                + string.Join("; ", shifts.Select(s => $"{s.Cashier} on {s.Terminal}")));
        var what = tickets.Count > 0 && shifts.Count > 0 ? "Settle the tickets and close the shifts first."
            : tickets.Count > 0 ? "Settle them first, or close empty ones." : "Close the shifts first.";
        throw new OpenTicketsException($"The day can't close while {string.Join(", and ", parts)}. {what}", tickets, shifts);
    }
}

/// <summary>GET /api/pos/day: what blocks the close, and the day's summary.</summary>
public sealed record DayStatusDto(
    bool Open, Guid? WorkPeriodId, DateTimeOffset? OpenedAt, string? OpenedBy, DayBlockersDto Blockers, DaySummaryDto? Summary);

/// <summary>The answer to a close: the summary printed, and when every terminal logs out.</summary>
public sealed record DayClosedDto(Guid WorkPeriodId, DateTimeOffset ClosedAt, DateTimeOffset LogoutAt, bool Printed, DaySummaryDto Summary);

public sealed class GetDayStatus(ITausiDb db, ICurrentSession session, IPermissionService permissions, DaySummaries summaries)
{
    public async Task<DayStatusDto> Execute(CancellationToken ct)
    {
        var user = session.RequireUser();
        if (await permissions.Check(user, PermissionKeys.DayOpenClose, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(PermissionKeys.DayOpenClose);
        var day = await db.WorkPeriods.AsNoTracking().FirstOrDefaultAsync(w => w.IsOpen, ct);
        if (day is null) return new DayStatusDto(false, null, null, null, new DayBlockersDto([], [], []), null);
        var opener = await db.Users.AsNoTracking().Where(u => u.Id == day.OpenedByUserId).Select(u => u.Name).FirstOrDefaultAsync(ct);
        var blockers = await DayClosing.Blockers(db, ct);
        return new DayStatusDto(true, day.Id, day.OpenedAt, opener, blockers, blockers.Blocked ? null : await summaries.For(day.Id, ct));
    }
}

/// <summary>
/// Closes the open work period (needs <c>day.open_close</c>): refused while any ticket or cashier
/// shift is open. Pending requests expire with it. The day report ("DAY REPORT · Z") prints on this
/// terminal's receipt printer, every terminal gets a 60-second warning (<c>day.closed</c>), and the
/// POS sessions still open are logged out after it. The development <c>close-day</c> command uses it too.
/// </summary>
public sealed class CloseWorkPeriod(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    DaySummaries summaries, CashPrints prints, IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    /// <summary>How long terminals get to finish before everyone is logged out.</summary>
    public static readonly TimeSpan LogoutWarning = TimeSpan.FromSeconds(60);

    public async Task<DayClosedDto> Execute(CancellationToken ct)
    {
        var user = session.RequireUser();
        var grant = await permissions.Require(user, PermissionKeys.DayOpenClose, approval.ApprovalToken, ct);
        return await Close(user.UserId, user.Name, user.TerminalId ?? session.Terminal?.TerminalId, grant.ApprovedByUserId, ct)
            ?? throw new ConflictException("The day isn't open.");
    }

    /// <summary>Without a session (the development command). Returns null when no day was open.</summary>
    public async Task<DayClosedDto?> Close(Guid? userId, string? userName, Guid? terminalId, Guid? approvedBy, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var period = await db.WorkPeriods.FirstOrDefaultAsync(w => w.IsOpen, ct);
        if (period is null) return null;
        await DayClosing.EnsureNoOpenTickets(db, ct);

        var now = clock.GetUtcNow();
        var expired = await db.ApprovalRequests.Where(r => r.Status == ApprovalStatus.Pending).ToListAsync(ct);
        var resolved = expired.Select(r => ApprovalExpiry.Expire(r, "The day was closed.", now)).ToList();
        var summary = await summaries.For(period.Id, ct);
        period.IsOpen = false;
        period.ClosedAt = now;
        period.ClosedByUserId = userId;
        period.ClosedTerminalId = terminalId;
        var printed = terminalId is { } t && await prints.Print(t,
            (style, business, local) => CashSlips.DayReport(summary, business, local(period.OpenedAt), local(now), userName ?? "-", style), ct) is not null;
        audit.Write(new AuditRecord(AuditActions.DayClosed, nameof(WorkPeriod), period.Id.ToString(), null,
            new { period.ClosedAt, summary.Tickets, summary.Gross, summary.Net, summary.Vat, expiredRequests = resolved.Count, printed },
            approvedBy));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var logoutAt = now + LogoutWarning;
        await events.Publish(PosEventNames.DayClosed, new { workPeriodId = period.Id, closedAt = now, logoutAt }, ct);
        await ApprovalExpiry.Publish(events, resolved, ct);
        return new DayClosedDto(period.Id, now, logoutAt, printed, summary);
    }
}

/// <summary>
/// After a close, logs out the POS sessions that were open when the day closed, once the
/// 60-second warning is over. The background sweep runs it every few seconds.
/// </summary>
public sealed class EndSessionsAfterDayClose(ITausiDb db, TimeProvider clock)
{
    public async Task<int> Run(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = now - CloseWorkPeriod.LogoutWarning;
        var periods = await db.WorkPeriods.Where(w => !w.IsOpen && w.SessionsEndedAt == null && w.ClosedAt != null && w.ClosedAt <= due)
            .ToListAsync(ct);
        var ended = 0;
        foreach (var period in periods)
        {
            ended += await db.UserSessions.Where(s => s.EndedAt == null && s.StartedAt <= period.ClosedAt)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.EndedAt, now).SetProperty(s => s.EndReason, SessionEndReasons.DayClosed), ct);
            period.SessionsEndedAt = now;
        }
        if (periods.Count > 0) await db.SaveChangesAsync(ct);
        return ended;
    }
}

/// <summary>
/// Closes a ticket whose lines were all voided or cancelled (total 0) as Cancelled, so it doesn't
/// keep the day open. Needs <c>day.open_close</c>; audited.
/// </summary>
public sealed class CloseEmptyTicket(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    ITicketDatabase tickets, IAuditLog audit, IPosEvents events, TicketEvents ticketEvents, TimeProvider clock)
{
    public async Task Execute(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        var grant = await permissions.Require(user, PermissionKeys.DayOpenClose, approval.ApprovalToken, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await tickets.LockRows([id], ct);
        var ticket = await db.Tickets.Include(k => k.Lines).Include(k => k.Payments).FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new NotFoundException("Ticket not found.");
        if (!ticket.IsOpen) throw new DomainException($"#{ticket.Number} is no longer open.");
        if (ticket.Lines.Any(l => l.Counts) || ticket.Total != 0)
            throw new DomainException($"#{ticket.Number} still has items on it. Settle it, or void them first.", "not_empty");
        if (ticket.Payments.Any(p => p.Counts))
            throw new DomainException($"#{ticket.Number} has payments. Remove them first.", "not_empty");

        var now = clock.GetUtcNow();
        ticket.Status = TicketStatus.Cancelled;
        ticket.ClearLock();
        ticket.Touch(now);
        var expired = await ApprovalExpiry.ForTickets(db, [ticket.Id], ApprovalExpiry.TicketClosed, now, ct);
        audit.Write(new AuditRecord(AuditActions.TicketClosedEmpty, nameof(Ticket), ticket.Id.ToString(),
            new { ticket = ticket.Number, status = TicketStatus.Open.ToString() },
            new { status = TicketStatus.Cancelled.ToString() }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await ticketEvents.Changed(ticket, ticket.TableId, ct);
        await ApprovalExpiry.Publish(events, expired, ct);
    }
}
