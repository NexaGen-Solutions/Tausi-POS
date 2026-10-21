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

public sealed record NewLineRequest(Guid MenuItemId, decimal Quantity);

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
    ITicketDatabase tickets, TicketEvents events, TimeProvider clock)
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
        if (request.Lines.Any(l => l.Quantity <= 0 || l.Quantity > 999))
            throw new DomainException("Each quantity must be more than 0 and at most 999.");

        DiningTable? table = null;
        if (request.TableId is { } tableId)
        {
            table = await db.Tables.Include(t => t.Area).FirstOrDefaultAsync(t => t.Id == tableId, ct);
            if (table is null || !table.Active || table.Area?.Active != true)
                throw new DomainException("That table is not in use.");
        }
        var customer = request.CustomerName?.Trim();
        if (customer is { Length: > Ticket.MaxCustomerNameLength })
            throw new DomainException($"The customer name can be at most {Ticket.MaxCustomerNameLength} characters.");

        var terminal = await db.Terminals.AsNoTracking().FirstAsync(t => t.Id == terminalId, ct);
        var lists = await db.PriceLists.AsNoTracking().ToListAsync(ct);
        var defaultList = lists.FirstOrDefault(l => l.IsDefault) ?? throw new ConflictException("There is no default price list.");
        var list = lists.FirstOrDefault(l => l.Id == terminal.PriceListId && l.Active) ?? defaultList;
        var businessVat = (await db.BusinessSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.VatRate ?? 16m;
        var itemIds = request.Lines.Select(l => l.MenuItemId).Distinct().ToList();
        var items = await db.MenuItems.AsNoTracking().Include(i => i.Prices)
            .Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

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
            if (!items.TryGetValue(line.MenuItemId, out var item) || !item.Active)
                throw new DomainException("An item on this ticket is no longer on the menu.");
            var price = MenuRules.PriceOn(item, list.Id) ?? MenuRules.PriceOn(item, defaultList.Id)
                ?? throw new DomainException($"{item.Name} has no price.");
            var rate = item.EffectiveVatRate(businessVat);
            var amounts = VatCalculator.ForLine(price, line.Quantity, rate);
            ticket.Lines.Add(new OrderLine
            {
                TicketId = ticket.Id,
                MenuItemId = item.Id,
                Name = item.Name,
                Quantity = line.Quantity,
                UnitPrice = price,
                VatRate = rate,
                Gross = amounts.Gross,
                Net = amounts.Net,
                Vat = amounts.Vat,
                AddedByUserId = user.UserId,
                AddedTerminalId = terminalId,
                AddedAt = now,
            });
        }
        ticket.TakeLock(terminalId, user.UserId, sessionId, now);
        ticket.Number = await tickets.NextNumber(ct);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);

        await events.Changed(ticket, ticket.TableId, ct);
        var holder = new LockHolder(terminalId, terminal.Name, user.UserId, user.Name);
        return await GetTicket.Detail(db, ticket, holder, ct);
    }
}

/// <summary>
/// Moves a ticket to another table. Only the table changes; if that table has tickets already,
/// this one becomes another ticket on it. Needs <c>ticket.move</c>. Nothing is printed.
/// </summary>
public sealed class MoveTicket(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    ITicketDatabase tickets, TicketLocks locks, IAuditLog audit, TicketEvents events, TimeProvider clock)
{
    public async Task<TicketSummaryDto> Execute(Guid id, MoveTicketRequest request, CancellationToken ct)
    {
        var user = session.RequireUser();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await tickets.LockRows([id], ct);

        var ticket = await db.Tickets.Include(k => k.Table).Include(k => k.Lines).FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new NotFoundException("Ticket not found.");
        if (!ticket.IsOpen) throw new DomainException($"#{ticket.Number} is not open.");
        var to = await db.Tables.Include(t => t.Area).FirstOrDefaultAsync(t => t.Id == request.ToTableId, ct);
        if (to is null || !to.Active || to.Area?.Active != true) throw new DomainException("That table is not in use.");
        if (to.Id == ticket.TableId) throw new DomainException($"#{ticket.Number} is already on {to.Name}.");
        await locks.EnsureFreeFor([ticket], user.TerminalId, ct);

        var grant = await permissions.Require(user, PermissionKeys.TicketMove, approval.ApprovalToken, ct);
        var from = ticket.Table;
        ticket.TableId = to.Id;
        ticket.Table = to;
        ticket.Touch(clock.GetUtcNow());
        audit.Write(new AuditRecord(AuditActions.TicketMoved, nameof(Ticket), ticket.Id.ToString(),
            new { ticket = ticket.Number, table = from?.Name },
            new { ticket = ticket.Number, table = to.Name }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await events.Changed(ticket, from?.Id, ct);
        await events.Changed(ticket, to.Id, ct);
        return Summary(ticket, user);
    }

    internal static TicketSummaryDto Summary(Ticket ticket, SessionUser user) =>
        new(ticket.Id, ticket.Number, ticket.TableId, ticket.CustomerName, ticket.OpenedByUserId, "", ticket.Total,
            ticket.OpenedAt, ticket.LastActivityAt, ticket.OpenedByUserId == user.UserId, null);
}

/// <summary>
/// Merges open tickets on one table into the oldest of them. Lines move across unchanged, the
/// others become Merged (their numbers stay in history), notes are joined. Needs <c>ticket.merge</c>.
/// </summary>
public sealed class MergeTickets(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    ITicketDatabase tickets, TicketLocks locks, IAuditLog audit, TicketEvents events, TimeProvider clock)
{
    public async Task<TicketSummaryDto> Execute(MergeTicketsRequest request, CancellationToken ct)
    {
        var user = session.RequireUser();
        var ids = request.TicketIds.Append(request.IntoTicketId).Distinct().ToList();
        if (ids.Count < 2) throw new DomainException("Pick at least two tickets to merge.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await tickets.LockRows(ids, ct);
        var found = await db.Tickets.Include(k => k.Lines).Where(k => ids.Contains(k.Id)).ToListAsync(ct);
        if (found.Count != ids.Count) throw new NotFoundException("Ticket not found.");

        var into = found.Single(k => k.Id == request.IntoTicketId);
        var others = found.Where(k => k.Id != into.Id).OrderBy(k => k.OpenedAt).ThenBy(k => k.Number).ToList();
        if (found.FirstOrDefault(k => !k.IsOpen) is { } closed) throw new DomainException($"#{closed.Number} is not open.");
        if (found.Any(k => k.TableId is null)) throw new DomainException("Takeaway tickets can't be merged.");
        if (found.Select(k => k.TableId).Distinct().Count() > 1) throw new DomainException("Only tickets on the same table can be merged.");
        var oldest = found.OrderBy(k => k.OpenedAt).ThenBy(k => k.Number).First();
        if (oldest.Id != into.Id) throw new DomainException($"The oldest ticket is kept: merge into #{oldest.Number}.");
        await locks.EnsureFreeFor(found, user.TerminalId, ct);

        var grant = await permissions.Require(user, PermissionKeys.TicketMerge, approval.ApprovalToken, ct);
        var before = found.OrderBy(k => k.Number).Select(k => new { ticket = k.Number, total = k.Total }).ToList();
        into.MergeIn(others, clock.GetUtcNow());
        audit.Write(new AuditRecord(AuditActions.TicketMerged, nameof(Ticket), into.Id.ToString(),
            new { tickets = before, total = before.Sum(b => b.total) },
            new { kept = into.Number, merged = others.Select(k => k.Number).ToList(), total = into.Total },
            grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        foreach (var other in others) await events.Changed(other, other.TableId, ct);
        await events.Changed(into, into.TableId, ct);
        return MoveTicket.Summary(into, user);
    }
}
