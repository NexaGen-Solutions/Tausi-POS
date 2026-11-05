using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Ledger;
using Tausi.Application.Settling;
using Tausi.Domain.Ledger;
using Tausi.Domain.Service;

namespace Tausi.Application.Money;

/// <summary>
/// Brings older books up to date at startup (the same code on every database): work periods and
/// journal entries get their business date, sales their cashier's shift, drawer pay-outs their
/// expense category, and (version 1.0.1) entries made during service move from their work period's
/// date to the calendar date they happened, a ticket served on an earlier date than it was paid
/// getting the same split as a new sale. Running it again changes nothing.
/// </summary>
public sealed class LedgerBackfill(ITausiDb db, ILedgerPostingService ledger)
{
    /// <summary>How many entries or tickets each save handles, so a large database never loads at once.</summary>
    public const int Batch = 500;

    public async Task<int> Run(CancellationToken ct)
    {
        var zone = await BusinessDates.Zone(db, ct);
        var changed = 0;

        var periods = await db.WorkPeriods.Where(p => p.BusinessDate == null).ToListAsync(ct);
        foreach (var period in periods) period.BusinessDate = BusinessDates.Of(period.OpenedAt, zone);
        changed += periods.Count;
        if (periods.Count > 0) await db.SaveChangesAsync(ct);

        var entries = await db.JournalEntries.Where(e => e.BusinessDate == null).ToListAsync(ct);
        foreach (var entry in entries) entry.BusinessDate = BusinessDates.Of(entry.Date, zone);
        changed += entries.Count;

        var sales = await db.JournalEntries.Where(e => e.SourceType == SalePosting.SourceType && e.ShiftId == null).ToListAsync(ct);
        if (sales.Count > 0)
        {
            var shifts = await db.Tickets.AsNoTracking().Where(k => k.SettledShiftId != null)
                .Select(k => new { Id = k.Id.ToString(), k.SettledShiftId }).ToListAsync(ct);
            var byTicket = shifts.ToDictionary(k => k.Id, k => k.SettledShiftId);
            foreach (var sale in sales.Where(s => s.SourceId != null && byTicket.ContainsKey(s.SourceId)))
            {
                sale.ShiftId = byTicket[sale.SourceId!];
                changed++;
            }
        }

        var expenses = await db.LedgerAccounts.Where(a => a.Code == LedgerAccountCodes.Expenses).Select(a => a.Id).FirstOrDefaultAsync(ct);
        var categories = await db.ExpenseCategories.AsNoTracking().ToListAsync(ct);
        var uncategorised = await db.JournalLines.Where(l => l.AccountId == expenses && l.ExpenseCategoryId == null).ToListAsync(ct);
        foreach (var line in uncategorised)
        {
            var category = categories.FirstOrDefault(c => string.Equals(c.Name, line.Memo, StringComparison.OrdinalIgnoreCase));
            if (category is null) continue;
            line.ExpenseCategoryId = category.Id;
            changed++;
        }

        await db.SaveChangesAsync(ct);
        changed += await MoveToCalendarDates(zone, ct);
        changed += await SplitSalesServedEarlier(zone, ct);
        return changed;
    }

    /// <summary>
    /// Version 1.0.1: entries made during service (with a work period) before it carried the work
    /// period's opening date. Each moves to the calendar date it happened; its old date stays in
    /// <see cref="JournalEntry.OldBusinessDate"/> (copied by the migration, which is also how these
    /// entries are told apart from newer ones). Admin-site entries keep the date chosen on the form.
    /// </summary>
    private async Task<int> MoveToCalendarDates(TimeZoneInfo zone, CancellationToken ct)
    {
        var moved = 0;
        var ids = await db.JournalEntries.AsNoTracking()
            .Where(e => e.WorkPeriodId != null && e.OldBusinessDate != null && e.BusinessDate == e.OldBusinessDate)
            .Select(e => e.Id).ToListAsync(ct);
        foreach (var chunk in ids.Chunk(Batch))
        {
            var batch = await db.JournalEntries.Where(e => chunk.Contains(e.Id)).ToListAsync(ct);
            foreach (var entry in batch)
            {
                var date = BusinessDates.Of(entry.Date, zone);
                if (date == entry.BusinessDate) continue;
                entry.BusinessDate = date;
                moved++;
            }
            await db.SaveChangesAsync(ct);
        }
        return moved;
    }

    /// <summary>
    /// Version 1.0.1: a ticket settled before it, with items served on an earlier date than it was
    /// paid, posted one sale entry on the date paid. Its items now move to the dates they were served
    /// through the posting service, as for a new sale: an entry on each such date (Dr Unsettled sales,
    /// Cr Sales and VAT) and one on the date paid that takes the same sales off it (Dr Sales and VAT,
    /// Cr Unsettled sales). Every entry balances; nothing posted earlier is changed or deleted.
    /// </summary>
    private async Task<int> SplitSalesServedEarlier(TimeZoneInfo zone, CancellationToken ct)
    {
        var split = 0;
        var alreadySplit = (await db.JournalLines.AsNoTracking()
            .Where(l => l.SourceType == SalePosting.SourceType && l.Account!.Code == LedgerAccountCodes.UnsettledSales)
            .Select(l => l.SourceId).Distinct().ToListAsync(ct)).ToHashSet();
        var ids = await db.JournalEntries.AsNoTracking()
            .Where(e => e.SourceType == SalePosting.SourceType && e.OldBusinessDate != null && e.ReversesEntryId == null && e.ReversedByEntryId == null)
            .Select(e => e.Id).ToListAsync(ct);
        foreach (var chunk in ids.Chunk(Batch))
        {
            var sales = await db.JournalEntries.AsNoTracking().Where(e => chunk.Contains(e.Id))
                .Select(e => new { e.Id, e.SourceId, e.BusinessDate, e.Date, e.WorkPeriodId, e.ShiftId }).ToListAsync(ct);
            var ticketIds = sales.Select(e => Guid.TryParse(e.SourceId, out var id) ? id : Guid.Empty).ToList();
            var tickets = await db.Tickets.AsNoTracking().Include(k => k.Lines).Where(k => ticketIds.Contains(k.Id))
                .ToDictionaryAsync(k => k.Id, ct);
            foreach (var sale in sales)
            {
                if (sale.SourceId is null || alreadySplit.Contains(sale.SourceId) || !Guid.TryParse(sale.SourceId, out var id)
                    || !tickets.TryGetValue(id, out var ticket) || sale.BusinessDate is not { } paidOn) continue;
                var counted = ticket.Lines.Where(l => l.Counts).ToList();
                var memo = $"Receipt {ticket.ReceiptNumber} · Ticket #{ticket.Number}";
                var takenOff = new List<JournalLineDraft>();
                foreach (var date in counted.Select(l => SalePosting.LineDate(l, zone)).Where(d => d != paidOn).Distinct().Order())
                {
                    var lines = new List<JournalLineDraft>();
                    SalePosting.SalesAndVat(counted.Where(l => SalePosting.LineDate(l, zone) == date).ToList(), lines);
                    var total = lines.Sum(l => l.Credit);
                    if (total == 0) continue;
                    await ledger.Post(new JournalEntryDraft(sale.Date, $"{memo} · served {date:ddd d MMM}", SalePosting.SourceType, sale.SourceId,
                        [JournalLineDraft.Dr(LedgerAccountCodes.UnsettledSales, total, "Served, not yet paid"), .. lines],
                        sale.WorkPeriodId, sale.ShiftId, date), ct);
                    takenOff.AddRange(lines.Select(l => JournalLineDraft.Dr(l.AccountCode, l.Credit, $"{l.Memo} · served {date:ddd d MMM}")));
                }
                if (takenOff.Count == 0) continue;
                await ledger.Post(new JournalEntryDraft(sale.Date, $"{memo} · items served earlier", SalePosting.SourceType, sale.SourceId,
                    [.. takenOff, JournalLineDraft.Cr(LedgerAccountCodes.UnsettledSales, takenOff.Sum(l => l.Debit), "Served earlier, paid now")],
                    sale.WorkPeriodId, sale.ShiftId, paidOn), ct);
                split++;
            }
            await db.SaveChangesAsync(ct);
        }
        return split;
    }
}
