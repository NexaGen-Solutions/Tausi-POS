using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Approvals;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Day;

/// <summary>A ticket that keeps the day open: "#1042 · Table 7 · edna · 1,370" (part paid ones too).</summary>
public sealed record OpenTicketDto(Guid Id, long Number, string Where, string Waiter, decimal Total, decimal Paid);

/// <summary>The day can't close: the open tickets are listed (the API returns them as <c>openTickets</c>).</summary>
public sealed class OpenTicketsException(string message, IReadOnlyList<OpenTicketDto> tickets)
    : ConflictException(message, DayClosing.OpenTicketsCode)
{
    public IReadOnlyList<OpenTicketDto> Tickets { get; } = tickets;
}

/// <summary>
/// The owner's rule: a day (work period) closes only when no ticket is open, part-paid ones
/// included, so every sale belongs to exactly one day. Tickets emptied by voids are closed with
/// <see cref="CloseEmptyTicket"/>.
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

    public static async Task EnsureNoOpenTickets(ITausiDb db, CancellationToken ct)
    {
        var open = await OpenTickets(db, ct);
        if (open.Count == 0) return;
        var list = string.Join("; ", open.Take(10).Select(k =>
            $"#{k.Number} {k.Where} · {k.Waiter} · {k.Total:#,##0.##}" + (k.Paid > 0 ? " (part paid)" : "")))
            + (open.Count > 10 ? "; …" : "");
        throw new OpenTicketsException(
            $"The day can't close while {open.Count} ticket{(open.Count == 1 ? " is" : "s are")} open: {list}. Settle them first, or close empty ones.",
            open);
    }
}

/// <summary>
/// Closes the open work period (needs <c>day.open_close</c>): refused while any ticket is open.
/// Task 9's close screen (with the cash-up) and the development <c>close-day</c> command use it.
/// </summary>
public sealed class CloseWorkPeriod(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public async Task<bool> Execute(CancellationToken ct)
    {
        var user = session.RequireUser();
        var grant = await permissions.Require(user, PermissionKeys.DayOpenClose, approval.ApprovalToken, ct);
        return await Close(user.UserId, user.TerminalId ?? session.Terminal?.TerminalId, grant.ApprovedByUserId, ct);
    }

    /// <summary>Without a session (the development command). Returns false when no day was open.</summary>
    public async Task<bool> Close(Guid? userId, Guid? terminalId, Guid? approvedBy, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var period = await db.WorkPeriods.FirstOrDefaultAsync(w => w.IsOpen, ct);
        if (period is null) return false;
        await DayClosing.EnsureNoOpenTickets(db, ct);
        period.IsOpen = false;
        period.ClosedAt = clock.GetUtcNow();
        period.ClosedByUserId = userId;
        period.ClosedTerminalId = terminalId;
        audit.Write(new AuditRecord(AuditActions.DayClosed, nameof(WorkPeriod), period.Id.ToString(),
            NewValue: new { period.ClosedAt }, ApprovedByUserId: approvedBy));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await events.Publish(PosEventNames.DayClosed, new { workPeriodId = period.Id, closedAt = period.ClosedAt }, ct);
        return true;
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
