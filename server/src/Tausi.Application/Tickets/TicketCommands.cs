using Tausi.Application.Approvals;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Menu;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

public sealed record NewLineRequest(Guid MenuItemId, decimal Quantity, IReadOnlyList<Guid>? OptionIds = null, string? Note = null);

public sealed record StartTicketRequest(Guid? TableId, string? CustomerName, IReadOnlyList<NewLineRequest> Lines, string? Note = null);

public sealed record MoveTicketRequest(Guid ToTableId);

public sealed record MergeTicketsRequest(IReadOnlyList<Guid> TicketIds, Guid IntoTicketId);

/// <summary>
/// Saves a draft ticket with its first lines. Tickets are never saved empty, and only inside an
/// open work period; the number comes from the database sequence at this moment. Prices come
/// from the terminal's price list, who and where from the session. The new ticket is locked for
/// this terminal, since it is on its screen.
/// </summary>
public sealed class StartTicket(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    ITicketDatabase tickets, MenuPricing pricing, TicketAccess access, TicketEvents events, TimeProvider clock)
{
    public async Task<TicketDetailDto> Execute(StartTicketRequest request, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (user.TerminalId is not { } terminalId || user.SessionId is not { } sessionId)
            throw new ForbiddenException("Tickets are opened on a terminal.", "terminal_required");
        await permissions.Require(user, PermissionKeys.OrderAdd, approval.ApprovalToken, ct);

        var day = await WorkPeriods.Current(db, ct)
            ?? throw new DomainException("The day isn't open yet. A supervisor must open it first.", TicketCodes.DayClosed);
        if (request.Lines is not { Count: > 0 })
            throw new DomainException("A ticket needs at least one line.");
        DiningTable? table = null;
        if (request.TableId is { } tableId)
        {
            table = await db.Tables.Include(t => t.Area).FirstOrDefaultAsync(t => t.Id == tableId, ct);
            if (table is null || !table.Active || table.Area?.Active != true)
                throw new DomainException("That table is not in use.");
            await access.EnsureTableFree(user, table.Id, table.Name, null, ct);
        }
        var customer = request.CustomerName?.Trim();
        if (customer is { Length: > Ticket.MaxCustomerNameLength })
            throw new DomainException($"The customer name can be at most {Ticket.MaxCustomerNameLength} characters.");

        var terminal = await db.Terminals.AsNoTracking().FirstAsync(t => t.Id == terminalId, ct);
        var pricer = await pricing.For(terminalId, ct);
        var now = clock.GetUtcNow();
        var ticket = new Ticket
        {
            TableId = table?.Id,
            CustomerName = string.IsNullOrEmpty(customer) ? null : customer,
            OpenedByUserId = user.UserId,
            OpenedAt = now,
            TerminalId = terminalId,
            WorkPeriodId = day.Id,
            LastActivityAt = now,
            Note = request.Note?.Trim() ?? "",
        };
        foreach (var line in request.Lines)
        {
            var priced = await pricer.Price(line.MenuItemId, line.OptionIds, ct);
            TicketLines.Add(ticket, priced, line.Quantity, TicketLines.CleanNote(line.Note), user.UserId, terminalId, now);
        }
        ticket.TakeLock(terminalId, user.UserId, sessionId, now);
        ticket.Number = await tickets.NextNumber(ct);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        await events.Changed(ticket, ticket.TableId, ct);
        return await TicketDetails.Build(db, ticket, new LockHolder(terminalId, terminal.Name, user.UserId, user.Name), ct, user.UserId);
    }
}

/// <summary>
/// Moves a ticket to another table. Only the table changes; if that table has tickets already,
/// this one becomes another ticket on it. Needs <c>ticket.move</c>. Nothing is printed.
/// </summary>
public sealed class MoveTicket(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    ITicketDatabase tickets, TicketLocks locks, TicketAccess access, IAuditLog audit, TicketEvents events, TimeProvider clock)
{
    public async Task<TicketSummaryDto> Execute(Guid id, MoveTicketRequest request, CancellationToken ct)
    {
        var user = session.RequireUser();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await tickets.LockRows([id], ct);
        var ticket = await Load(id, ct);
        var to = await Check(ticket, request, user.TerminalId, ct);
        await EnsureAllowed(user, ticket, to, ct);
        await PendingRequests.EnsureNone(db, ticket.Id, null, ApprovalKind.Move, null, ct);
        var grant = await permissions.Require(user, PermissionKeys.TicketMove, approval.ApprovalToken, ct);
        var from = Apply(ticket, to, user.UserId, user.TerminalId, grant.ApprovedByUserId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await Announce(ticket, from, ct);
        return Summary(ticket, user);
    }

    /// <summary>Whose ticket and whose table: also checked when a move request is made.</summary>
    public async Task EnsureAllowed(SessionUser user, Ticket ticket, DiningTable to, CancellationToken ct)
    {
        await access.EnsureMayChange(user, ticket, ct);
        await access.EnsureTableFree(user, to.Id, to.Name, ticket.Id, ct);
    }

    public async Task<Ticket> Load(Guid id, CancellationToken ct) =>
        await db.Tickets.Include(k => k.Table).Include(k => k.Lines).FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new NotFoundException("Ticket not found.");

    /// <summary>The checks, also run when a move request is made and when it is approved.</summary>
    public async Task<DiningTable> Check(Ticket ticket, MoveTicketRequest request, Guid? terminalId, CancellationToken ct)
    {
        if (!ticket.IsOpen) throw new DomainException($"#{ticket.Number} is not open.");
        var to = await db.Tables.Include(t => t.Area).FirstOrDefaultAsync(t => t.Id == request.ToTableId, ct);
        if (to is null || !to.Active || to.Area?.Active != true) throw new DomainException("That table is not in use.");
        if (to.Id == ticket.TableId) throw new DomainException($"#{ticket.Number} is already on {to.Name}.");
        await locks.EnsureFreeFor([ticket], terminalId, ct);
        return to;
    }

    /// <returns>The table it was on.</returns>
    public DiningTable? Apply(Ticket ticket, DiningTable to, Guid userId, Guid? terminalId, Guid? approvedBy)
    {
        var from = ticket.Table;
        ticket.TableId = to.Id;
        ticket.Table = to;
        ticket.Touch(clock.GetUtcNow());
        audit.Write(new AuditRecord(AuditActions.TicketMoved, nameof(Ticket), ticket.Id.ToString(),
            new { ticket = ticket.Number, table = from?.Name },
            new { ticket = ticket.Number, table = to.Name }, approvedBy, userId, terminalId));
        return from;
    }

    public async Task Announce(Ticket ticket, DiningTable? from, CancellationToken ct)
    {
        await events.Changed(ticket, from?.Id, ct);
        await events.Changed(ticket, ticket.TableId, ct);
    }

    internal static TicketSummaryDto Summary(Ticket ticket, SessionUser user) =>
        new(ticket.Id, ticket.Number, ticket.TableId, ticket.CustomerName, ticket.OpenedByUserId, "", ticket.Total,
            ticket.OpenedAt, ticket.LastActivityAt, ticket.OpenedByUserId == user.UserId, null);
}

/// <summary>
/// Merges open tickets on one table into the oldest of them. Lines move across unchanged, the
/// others become Merged (their numbers stay in history), notes are joined, and requests waiting on
/// the merged-away tickets expire. Needs <c>ticket.merge</c>.
/// </summary>
public sealed class MergeTickets(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    ITicketDatabase tickets, TicketLocks locks, TicketAccess access, IAuditLog audit, TicketEvents events, TimeProvider clock)
{
    public async Task<TicketSummaryDto> Execute(MergeTicketsRequest request, CancellationToken ct)
    {
        var user = session.RequireUser();
        var ids = Ids(request);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await tickets.LockRows(ids, ct);
        var found = await Load(ids, ct);
        await Check(found, request, user.TerminalId, ct);
        await EnsureAllowed(user, found, ct);
        await PendingRequests.EnsureNone(db, request.IntoTicketId, null, ApprovalKind.Merge, null, ct);
        var grant = await permissions.Require(user, PermissionKeys.TicketMerge, approval.ApprovalToken, ct);
        var (into, others) = Apply(found, request, user.UserId, user.TerminalId, grant.ApprovedByUserId);
        var expired = await ApprovalExpiry.ForTickets(db, others.Select(k => k.Id).ToList(), ApprovalExpiry.TicketMerged, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await Announce(into, others, ct);
        await ApprovalExpiry.Publish(events.Events, expired, ct);
        return MoveTicket.Summary(into, user);
    }

    /// <summary>Every ticket merged must be one the user may change: also checked when a merge request is made.</summary>
    public async Task EnsureAllowed(SessionUser user, IEnumerable<Ticket> found, CancellationToken ct)
    {
        foreach (var ticket in found) await access.EnsureMayChange(user, ticket, ct);
    }

    public static List<Guid> Ids(MergeTicketsRequest request)
    {
        var ids = (request.TicketIds ?? []).Append(request.IntoTicketId).Distinct().ToList();
        if (ids.Count < 2) throw new DomainException("Pick at least two tickets to merge.");
        return ids;
    }

    public async Task<List<Ticket>> Load(List<Guid> ids, CancellationToken ct)
    {
        var found = await db.Tickets.Include(k => k.Lines).Include(k => k.Table).Where(k => ids.Contains(k.Id)).ToListAsync(ct);
        if (found.Count != ids.Count) throw new NotFoundException("Ticket not found.");
        return found;
    }

    /// <summary>The checks, also run when a merge request is made and when it is approved.</summary>
    public async Task Check(List<Ticket> found, MergeTicketsRequest request, Guid? terminalId, CancellationToken ct)
    {
        if (found.FirstOrDefault(k => !k.IsOpen) is { } closed) throw new DomainException($"#{closed.Number} is not open.");
        if (found.Any(k => k.TableId is null)) throw new DomainException("Takeaway tickets can't be merged.");
        if (found.Select(k => k.TableId).Distinct().Count() > 1) throw new DomainException("Only tickets on the same table can be merged.");
        var oldest = found.OrderBy(k => k.OpenedAt).ThenBy(k => k.Number).First();
        if (oldest.Id != request.IntoTicketId) throw new DomainException($"The oldest ticket is kept: merge into #{oldest.Number}.");
        if (found.FirstOrDefault(k => k.Id != request.IntoTicketId && k.DiscountKind is not null) is { } discounted)
            throw new DomainException($"#{discounted.Number} has its own discount. Remove it first, then merge.");
        await locks.EnsureFreeFor(found, terminalId, ct);
    }

    public (Ticket Into, List<Ticket> Others) Apply(List<Ticket> found, MergeTicketsRequest request, Guid userId, Guid? terminalId, Guid? approvedBy)
    {
        var into = found.Single(k => k.Id == request.IntoTicketId);
        var others = found.Where(k => k.Id != into.Id).OrderBy(k => k.OpenedAt).ThenBy(k => k.Number).ToList();
        var before = found.OrderBy(k => k.Number).Select(k => new { ticket = k.Number, total = k.Total }).ToList();
        into.MergeIn(others, clock.GetUtcNow());
        audit.Write(new AuditRecord(AuditActions.TicketMerged, nameof(Ticket), into.Id.ToString(),
            new { tickets = before, total = before.Sum(b => b.total) },
            new { kept = into.Number, merged = others.Select(k => k.Number).ToList(), total = into.Total },
            approvedBy, userId, terminalId));
        return (into, others);
    }

    public async Task Announce(Ticket into, List<Ticket> others, CancellationToken ct)
    {
        foreach (var other in others) await events.Changed(other, other.TableId, ct);
        await events.Changed(into, into.TableId, ct);
    }
}
