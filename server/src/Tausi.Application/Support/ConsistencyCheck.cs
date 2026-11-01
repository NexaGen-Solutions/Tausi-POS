using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Security;
using Tausi.Application.Settling;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Support;

public sealed record ConsistencyProblem(string Area, string Detail);

public sealed record ConsistencyReport(
    DateTimeOffset CheckedAt, int SettledTickets, int JournalEntries, decimal TotalDebit, decimal TotalCredit,
    IReadOnlyList<ConsistencyProblem> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Checks the real data agrees with itself, for after a power cut (TausiServer check), after a load test,
/// or for support: the books balance (each entry and the trial balance), every settled ticket is paid in
/// full and has exactly one sale entry, and every item's stock on hand is the sum of its movements.
/// Reads only.
/// </summary>
public sealed class ConsistencyCheck(ITausiDb db, TimeProvider clock, ICurrentSession? session = null, IPermissionService? permissions = null)
{
    /// <summary>The most problems listed per area; the counts say how many there were.</summary>
    public const int MaxListed = 20;

    /// <summary>From the admin site (settings.manage).</summary>
    public async Task<ConsistencyReport> RunAsAdmin(CancellationToken ct)
    {
        await permissions!.Require(session!.RequireUser(), PermissionKeys.SettingsManage, null, ct);
        return await Run(ct);
    }

    /// <summary>From the command line or a test: no session.</summary>
    public async Task<ConsistencyReport> Run(CancellationToken ct)
    {
        var problems = new List<ConsistencyProblem>();
        void Add(string area, IEnumerable<string> details)
        {
            var list = details.ToList();
            problems.AddRange(list.Take(MaxListed).Select(d => new ConsistencyProblem(area, d)));
            if (list.Count > MaxListed) problems.Add(new ConsistencyProblem(area, $"…and {list.Count - MaxListed} more."));
        }

        // The books: each entry balances, and so does the whole ledger.
        var entries = await db.JournalLines.AsNoTracking()
            .GroupBy(l => l.JournalEntryId)
            .Select(g => new { Entry = g.Key, Debit = g.Sum(l => l.Debit), Credit = g.Sum(l => l.Credit), Lines = g.Count() })
            .ToListAsync(ct);
        Add("ledger", entries.Where(e => e.Debit != e.Credit || e.Lines < 2)
            .Select(e => $"Journal entry {e.Entry} doesn't balance: debit {e.Debit:0.00}, credit {e.Credit:0.00}."));
        var debit = entries.Sum(e => e.Debit);
        var credit = entries.Sum(e => e.Credit);
        if (debit != credit) Add("ledger", [$"The trial balance is off by {debit - credit:0.00} (debit {debit:0.00}, credit {credit:0.00})."]);

        // Settled tickets: paid in full, and one sale entry each (none when nothing was due).
        var settled = await db.Tickets.AsNoTracking().Include(t => t.Lines).Include(t => t.Payments)
            .Where(t => t.Status == TicketStatus.Settled).ToListAsync(ct);
        var saleEntries = (await db.JournalEntries.AsNoTracking()
                .Where(e => e.SourceType == SalePosting.SourceType && e.ReversesEntryId == null && e.ReversedByEntryId == null)
                .Select(e => e.SourceId).ToListAsync(ct))
            .GroupBy(id => id ?? "").ToDictionary(g => g.Key, g => g.Count());
        Add("tickets", settled.Where(t => t.Paid != t.AmountDue)
            .Select(t => $"#{t.Number} is settled for {t.AmountDue:0.00} but its payments come to {t.Paid:0.00}."));
        Add("tickets", settled
            .Select(t => (Ticket: t, Entries: saleEntries.GetValueOrDefault(t.Id.ToString())))
            .Where(x => x.Entries != (x.Ticket.AmountDue > 0 ? 1 : 0))
            .Select(x => $"#{x.Ticket.Number} has {x.Entries} sale journal entries (it should have {(x.Ticket.AmountDue > 0 ? 1 : 0)})."));
        Add("tickets", settled.Where(t => t.ReceiptNumber is null).Select(t => $"#{t.Number} is settled without a receipt number."));

        // Stock: the cached on hand is the sum of the movements.
        var moved = (await db.StockMovements.AsNoTracking()
                .GroupBy(m => new { m.StockItemId, m.LocationId })
                .Select(g => new { g.Key.StockItemId, g.Key.LocationId, Sum = g.Sum(m => m.Quantity) })
                .ToListAsync(ct))
            .ToDictionary(m => (m.StockItemId, m.LocationId), m => m.Sum);
        var levels = await db.StockLevels.AsNoTracking().ToListAsync(ct);
        Add("stock", levels.Where(l => l.OnHand != moved.GetValueOrDefault((l.StockItemId, l.LocationId)))
            .Select(l => $"Stock item {l.StockItemId} at {l.LocationId}: on hand {l.OnHand} but its movements add up to {moved.GetValueOrDefault((l.StockItemId, l.LocationId))}."));

        return new ConsistencyReport(clock.GetUtcNow(), settled.Count, entries.Count, debit, credit, problems);
    }
}
