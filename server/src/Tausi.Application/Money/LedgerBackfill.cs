using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Settling;
using Tausi.Domain.Ledger;

namespace Tausi.Application.Money;

/// <summary>
/// Brings books from before Task 12 up to date, once, at startup (the same code on every database):
/// work periods and journal entries get their business date (the period's opening date, else the
/// entry's own date), sales get their cashier's shift, and drawer pay-outs their expense category.
/// Running it again changes nothing.
/// </summary>
public sealed class LedgerBackfill(ITausiDb db)
{
    public async Task<int> Run(CancellationToken ct)
    {
        var zone = await BusinessDates.Zone(db, ct);
        var changed = 0;

        var periods = await db.WorkPeriods.Where(p => p.BusinessDate == null).ToListAsync(ct);
        foreach (var period in periods) period.BusinessDate = BusinessDates.Of(period.OpenedAt, zone);
        changed += periods.Count;
        if (periods.Count > 0) await db.SaveChangesAsync(ct);

        var dates = await db.WorkPeriods.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.BusinessDate, ct);
        var entries = await db.JournalEntries.Where(e => e.BusinessDate == null).ToListAsync(ct);
        foreach (var entry in entries)
            entry.BusinessDate = (entry.WorkPeriodId is { } p ? dates.GetValueOrDefault(p) : null) ?? BusinessDates.Of(entry.Date, zone);
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
        return changed;
    }
}
