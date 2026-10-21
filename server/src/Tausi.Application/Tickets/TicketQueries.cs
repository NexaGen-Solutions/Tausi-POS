using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

/// <summary>How long a table's open tickets have gone without activity, when that is past the limit.</summary>
public static class TableIdle
{
    /// <summary>
    /// A table is idle when one of its open tickets has had no activity (a line added, voided or
    /// printed, a move or a merge) for the limit. Returns the longest idle time in whole minutes.
    /// </summary>
    public static int? Minutes(IEnumerable<DateTimeOffset> lastActivity, DateTimeOffset now, TimeSpan limit)
    {
        var oldest = lastActivity.DefaultIfEmpty(now).Min();
        var idle = now - oldest;
        return idle >= limit ? (int)idle.TotalMinutes : null;
    }
}

/// <summary>
/// The table map: active areas and tables with their open tickets, totals and edit locks.
/// Tables and tickets come from one database query however many tables there are.
/// </summary>
public sealed class GetTableMap(ITausiDb db, ICurrentSession session, TableMapVersion version, TimeProvider clock)
{
    public async Task<TableMapDto> Execute(IReadOnlyCollection<Guid>? onlyTableIds, CancellationToken ct)
    {
        var me = session.RequireUser();
        // Read the version first: an event published while we query is then seen as newer.
        var current = version.Current;
        var now = clock.GetUtcNow();
        var settings = await PosSettings.Load(db, ct);
        var day = await WorkPeriods.Current(db, ct);

        var query = db.Tables.AsNoTracking().Where(t => t.Active && t.Area!.Active);
        if (onlyTableIds is { Count: > 0 }) query = query.Where(t => onlyTableIds.Contains(t.Id));

        var rows = await query
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.AreaId,
                t.SortOrder,
                AreaName = t.Area!.Name,
                AreaSort = t.Area.SortOrder,
                Tickets = db.Tickets
                    .Where(k => k.TableId == t.Id && k.Status == TicketStatus.Open)
                    .Select(k => new TicketRow(
                        k.Id, k.Number, k.TableId, k.CustomerName, k.OpenedByUserId,
                        db.Users.Where(u => u.Id == k.OpenedByUserId).Select(u => u.Name).FirstOrDefault(),
                        k.Lines.Where(l => l.VoidedAt == null && l.Status != OrderLineStatus.Cancelled && l.Status != OrderLineStatus.Voided).Select(l => l.Gross - l.Discount).ToList(),
                        k.OpenedAt, k.LastActivityAt,
                        k.OpenedByUserId == me.UserId || k.Lines.Any(l => l.AddedByUserId == me.UserId),
                        k.LockTerminalId, k.LockUserId, k.LockExpiresAt,
                        db.UserSessions.Any(s => s.Id == k.LockSessionId && s.EndedAt == null),
                        db.Terminals.Where(x => x.Id == k.LockTerminalId).Select(x => x.Name).FirstOrDefault(),
                        db.Users.Where(u => u.Id == k.LockUserId).Select(u => u.Name).FirstOrDefault(),
                        k.Lines.Count(l => l.Status == OrderLineStatus.New)))
                    .ToList(),
            })
            .ToListAsync(ct);

        var ordered = rows.OrderBy(r => r.AreaSort).ThenBy(r => r.AreaName).ThenBy(r => r.SortOrder)
            .ThenBy(r => r.Name, NaturalOrder.Instance).ToList();
        var areas = ordered.Select(r => new AreaDto(r.AreaId, r.AreaName, r.AreaSort)).DistinctBy(a => a.Id).ToList();
        var tables = ordered.Select(r =>
        {
            var tickets = r.Tickets.OrderBy(k => k.OpenedAt).ThenBy(k => k.Number).Select(k => k.ToDto(now)).ToList();
            return new TableTileDto(r.Id, r.Name, r.AreaId, r.SortOrder, tickets,
                tickets.Count == 0 ? null : TableIdle.Minutes(tickets.Select(k => k.LastActivityAt), now, settings.TableIdleLimit));
        }).ToList();

        return new TableMapDto(current, now, day is not null, settings.TableIdleMinutes, areas, tables);
    }
}

/// <summary>Open takeaway tickets (no table), oldest first.</summary>
public sealed class GetTakeawayTickets(ITausiDb db, ICurrentSession session, TimeProvider clock)
{
    public async Task<IReadOnlyList<TicketSummaryDto>> Execute(CancellationToken ct)
    {
        var me = session.RequireUser();
        var now = clock.GetUtcNow();
        var rows = await db.Tickets.AsNoTracking()
            .Where(k => k.TableId == null && k.Status == TicketStatus.Open)
            .Select(k => new TicketRow(
                k.Id, k.Number, k.TableId, k.CustomerName, k.OpenedByUserId,
                db.Users.Where(u => u.Id == k.OpenedByUserId).Select(u => u.Name).FirstOrDefault(),
                k.Lines.Where(l => l.VoidedAt == null && l.Status != OrderLineStatus.Cancelled && l.Status != OrderLineStatus.Voided).Select(l => l.Gross - l.Discount).ToList(),
                k.OpenedAt, k.LastActivityAt,
                k.OpenedByUserId == me.UserId || k.Lines.Any(l => l.AddedByUserId == me.UserId),
                k.LockTerminalId, k.LockUserId, k.LockExpiresAt,
                db.UserSessions.Any(s => s.Id == k.LockSessionId && s.EndedAt == null),
                db.Terminals.Where(x => x.Id == k.LockTerminalId).Select(x => x.Name).FirstOrDefault(),
                db.Users.Where(u => u.Id == k.LockUserId).Select(u => u.Name).FirstOrDefault(),
                k.Lines.Count(l => l.Status == OrderLineStatus.New)))
            .ToListAsync(ct);
        return rows.OrderBy(k => k.OpenedAt).ThenBy(k => k.Number).Select(k => k.ToDto(now)).ToList();
    }
}

/// <summary>One ticket with its lines, for the order screen. Any user with order.add may open any open ticket.</summary>
public sealed class GetTicket(ITausiDb db, ICurrentSession session, TicketLocks locks)
{
    public async Task<TicketDetailDto> Execute(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        var ticket = await db.Tickets.AsNoTracking().Include(k => k.Lines).Include(k => k.Table)
            .FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new NotFoundException("Ticket not found.");
        return await TicketDetails.Build(db, ticket, await locks.Holder(ticket, ct), ct, user.UserId);
    }
}

/// <summary>The order screen's view of a ticket: cancelled lines are left out, failed print jobs included.</summary>
public static class TicketDetails
{
    /// <summary>For the terminal that has just changed the ticket (it holds the lock).</summary>
    public static Task<TicketDetailDto> Mine(ITausiDb db, Ticket ticket, SessionUser user, CancellationToken ct) =>
        Build(db, ticket, new LockHolder(user.TerminalId!.Value, user.TerminalName ?? "", user.UserId, user.Name), ct, user.UserId);

    /// <param name="viewer">Who is looking, to mark their own pending requests ("Cancel request").</param>
    public static async Task<TicketDetailDto> Build(ITausiDb db, Ticket ticket, LockHolder? holder, CancellationToken ct, Guid? viewer = null)
    {
        var shown = ticket.Lines.Where(l => l.Status != OrderLineStatus.Cancelled).ToList();
        var userIds = shown.Select(l => l.AddedByUserId).Append(ticket.OpenedByUserId).Distinct().ToList();
        var terminalIds = shown.Select(l => l.AddedTerminalId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var terminals = await db.Terminals.AsNoTracking().Where(t => terminalIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var tableName = ticket.Table?.Name
            ?? (ticket.TableId is { } tableId ? await db.Tables.AsNoTracking().Where(t => t.Id == tableId).Select(t => t.Name).FirstOrDefaultAsync(ct) : null);
        var failed = await db.PrintJobs.AsNoTracking()
            .Where(j => j.TicketId == ticket.Id && j.Status == Domain.Printing.PrintJobStatus.Failed)
            .OrderBy(j => j.CreatedAt)
            .Select(j => new FailedJobDto(j.Id, j.Kind.ToString(), j.Printer!.Name, j.LastError))
            .ToListAsync(ct);

        var pending = await db.ApprovalRequests.AsNoTracking()
            .Where(r => r.TicketId == ticket.Id && r.Status == ApprovalStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Kind, r.Summary, r.OrderLineId, r.RequestedByUserId, By = db.Users.Where(u => u.Id == r.RequestedByUserId).Select(u => u.Name).FirstOrDefault() })
            .ToListAsync(ct);
        var pendingDtos = pending.Select(r => (r.OrderLineId, Dto: new PendingRequestDto(r.Id, r.Kind.ToString(), r.Summary, r.By ?? "?",
            viewer is { } v && r.RequestedByUserId == v))).ToList();

        // Lines in the order they were added; a part split off (void, gift, price change) sits after its line.
        var lines = shown.OrderBy(l => l.AddedAt).ThenBy(l => l.Id).Select(l => new OrderLineDto(
            l.Id, l.MenuItemId, l.Name, l.ShortName, l.Options, l.Note, l.Quantity, l.UnitPrice, l.Gross, l.Status.ToString(),
            l.PrintRound, l.AddedByUserId, users.GetValueOrDefault(l.AddedByUserId, "?"),
            terminals.GetValueOrDefault(l.AddedTerminalId, "?"), l.AddedAt, l.PrintedAt, l.IsVoided,
            l.IsGift, l.OriginalUnitPrice, l.Discount, l.VoidReason,
            pendingDtos.FirstOrDefault(p => p.OrderLineId == l.Id).Dto)).ToList();

        return new TicketDetailDto(
            ticket.Id, ticket.Number, ticket.Status.ToString(), ticket.TableId, tableName, ticket.CustomerName,
            users.GetValueOrDefault(ticket.OpenedByUserId, "?"), ticket.OpenedAt, ticket.LastActivityAt, ticket.Note,
            ticket.Total, lines,
            holder is null ? null : new LockDto(holder.TerminalId, holder.TerminalName, holder.UserId, holder.UserName),
            ticket.RowVersion, ticket.BillNumber, ticket.NewLineCount, failed,
            ticket.Subtotal, ticket.DiscountTotal, TicketPrints.DiscountLabel(ticket),
            pendingDtos.Where(p => p.OrderLineId is null).Select(p => p.Dto).ToList());
    }
}

/// <summary>A ticket as read by the map queries (totals are summed here; SQLite cannot sum decimals).</summary>
internal sealed record TicketRow(
    Guid Id, long Number, Guid? TableId, string? CustomerName, Guid OpenedById, string? OpenedBy,
    List<decimal> LineAmounts, DateTimeOffset OpenedAt, DateTimeOffset LastActivityAt, bool Mine,
    Guid? LockTerminalId, Guid? LockUserId, DateTimeOffset? LockExpiresAt, bool LockSessionLive,
    string? LockTerminal, string? LockUser, int NewLines)
{
    public TicketSummaryDto ToDto(DateTimeOffset now)
    {
        var locked = LockTerminalId is { } terminal && LockUserId is { } user && LockExpiresAt > now && LockSessionLive
            ? new LockDto(terminal, LockTerminal ?? "another terminal", user, LockUser ?? "someone")
            : null;
        return new TicketSummaryDto(Id, Number, TableId, CustomerName, OpenedById, OpenedBy ?? "?",
            LineAmounts.Sum(), OpenedAt, LastActivityAt, Mine, locked, NewLines);
    }
}

/// <summary>Sorts "2" before "10" and "Pool 2" before "Pool 10".</summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.Ordinal);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i; while (i < x.Length && char.IsDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
