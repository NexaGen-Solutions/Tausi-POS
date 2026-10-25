using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Cash;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Settling;
using Tausi.Application.Stock;
using Tausi.Domain.Ledger;
using Tausi.Domain.Money;
using Tausi.Domain.Service;

namespace Tausi.Application.Money;

public sealed record NamedAmount(string Name, decimal Amount);

/// <summary>
/// A business day's profit, from the journal lines of that date only (never estimated apart):
/// net = gross profit − expenses − stock loss and cash differences − bank charges + other income.
/// Sales (incl. VAT) are the items sold, as in every report; rounding is its own figure and part of
/// net sales (it is what customers paid, without VAT): net sales = sales − VAT + rounding.
/// </summary>
public sealed record DailySummaryDto(
    DateOnly Date, decimal SalesInclVat, decimal Vat, decimal NetSales, decimal CostOfSales, decimal GrossProfit, decimal? GrossMarginPercent,
    decimal Expenses, IReadOnlyList<NamedAmount> ExpensesByCategory, decimal StockLossAndCash, IReadOnlyList<NamedAmount> StockLossParts,
    decimal BankCharges, decimal OtherIncome, decimal Net, IReadOnlyList<string> NoRecipeItems, decimal Rounding = 0);

/// <summary>The tiles, as at the end of the day: money in the safe and drawers, M-Pesa, the bank and what suppliers are owed.</summary>
public sealed record CashBookTiles(decimal CashOnHand, decimal InOpenDrawers, decimal MpesaReceived, decimal MpesaBalance, decimal Bank, decimal OwedToSuppliers);

/// <summary>
/// One line of the cash book in plain words: "Cash sales · CAROL shift", "Paid KBL", "Banked cash".
/// Sales are one line per shift and payment type; everything else is one line per entry.
/// </summary>
public sealed record CashBookRow(
    Guid EntryId, DateTimeOffset At, string Text, string Account, decimal? In, decimal? Out, bool Late, bool Reversed, bool Reversal,
    Guid? MoneyDocumentId, int Entries);

public sealed record CashBookDto(DateOnly Date, CashBookTiles Tiles, IReadOnlyList<CashBookRow> Rows, DailySummaryDto Summary, bool CanRecord);

public sealed record EntryLineDto(string Account, string Code, decimal Debit, decimal Credit, string Memo, string? Category);

public sealed record EntryDetailDto(
    Guid Id, DateOnly? BusinessDate, DateTimeOffset At, string Memo, string? By, bool Late, Guid? ReversesEntryId, Guid? ReversedByEntryId,
    IReadOnlyList<EntryLineDto> Lines, MoneyDocumentDto? Document);

/// <summary>The money side of a business day: tiles, entries, the daily summary (wireframe 14).</summary>
public sealed class CashBook(ITausiDb db, ICurrentSession session, MoneyAccess access, MoneyActions money, IPermissionService permissions)
{
    public async Task<CashBookDto> Day(DateOnly date, bool all, CancellationToken ct)
    {
        var user = session.RequireUser();
        await access.RequireView(user, ct);
        var canRecord = await permissions.Check(user, Domain.People.PermissionKeys.CashBook, ct) != Domain.People.PermissionValue.NotAllowed;
        return new CashBookDto(date, await Tiles(db, date, ct), await Rows(date, all, ct), await Summaries.For(db, date, includeLate: true, ct), canRecord);
    }

    public static async Task<CashBookTiles> Tiles(ITausiDb db, DateOnly date, CancellationToken ct)
    {
        var balances = await Balances(db, date, ct);
        var mpesaToday = await db.JournalLines.AsNoTracking()
            .Where(l => l.Account!.Code == LedgerAccountCodes.MPesa)
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l.Debit, e.BusinessDate, e.SourceType })
            .Where(x => x.BusinessDate == date && x.SourceType == SalePosting.SourceType).SumAsync(x => x.Debit, ct);
        decimal B(string code) => balances.GetValueOrDefault(code);
        return new CashBookTiles(B(LedgerAccountCodes.Safe) + B(LedgerAccountCodes.Cash), B(LedgerAccountCodes.Cash), mpesaToday,
            B(LedgerAccountCodes.MPesa), B(LedgerAccountCodes.Bank), -B(LedgerAccountCodes.SupplierPayables));
    }

    /// <summary>Debits − credits per account code up to the end of the date.</summary>
    public static async Task<Dictionary<string, decimal>> Balances(ITausiDb db, DateOnly date, CancellationToken ct) =>
        await db.JournalLines.AsNoTracking()
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { Code = l.Account!.Code, l.Debit, l.Credit, e.BusinessDate })
            .Where(x => x.BusinessDate <= date)
            .GroupBy(x => x.Code).Select(g => new { g.Key, Balance = g.Sum(x => x.Debit - x.Credit) })
            .ToDictionaryAsync(x => x.Key, x => x.Balance, ct);

    private async Task<IReadOnlyList<CashBookRow>> Rows(DateOnly date, bool all, CancellationToken ct)
    {
        var entries = await db.JournalEntries.AsNoTracking().Include(e => e.Lines).ThenInclude(l => l.Account)
            .Where(e => e.BusinessDate == date).OrderBy(e => e.CreatedAt).ToListAsync(ct);
        var shiftIds = entries.Where(e => e.ShiftId != null).Select(e => e.ShiftId!.Value).Distinct().ToList();
        var cashiers = await db.CashierShifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id))
            .Join(db.Users, s => s.CashierUserId, u => u.Id, (s, u) => new { s.Id, u.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var documentIds = entries.Where(e => e.SourceType == MoneyActions.LedgerSource && e.SourceId != null)
            .Select(e => Guid.TryParse(e.SourceId!.Split(':')[0], out var id) ? id : Guid.Empty).Distinct().ToList();
        var documents = await db.MoneyDocuments.AsNoTracking().Where(d => documentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var categories = await db.ExpenseCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        string Shift(Guid? id) => id is { } s && cashiers.TryGetValue(s, out var name) ? $" · {name} shift" : "";

        var rows = new List<CashBookRow>();
        // Sales: one line per shift and payment type.
        foreach (var group in entries.Where(e => e.SourceType == SalePosting.SourceType).GroupBy(e => e.ShiftId))
            foreach (var (code, label) in new[] { (LedgerAccountCodes.Cash, "Cash"), (LedgerAccountCodes.MPesa, "M-Pesa"), (LedgerAccountCodes.CardClearing, "Card") })
            {
                var amount = group.SelectMany(e => e.Lines).Where(l => l.Account!.Code == code).Sum(l => l.Debit - l.Credit);
                if (amount == 0) continue;
                rows.Add(new CashBookRow(group.Last().Id, group.Max(e => e.CreatedAt), $"{label} sales{Shift(group.Key)}", label, amount, null,
                    group.Any(e => e.IsLate), false, false, null, group.Count()));
            }

        // Cost of sales and voids: one line each for the day (All entries only).
        if (all)
            foreach (var (source, text) in new[] { (StockSelling.SaleSource, "Cost of sales"), (StockSelling.VoidSource, "Voids (cost)") })
            {
                var group = entries.Where(e => e.SourceType == source).ToList();
                if (group.Count == 0) continue;
                foreach (var byAccount in group.SelectMany(e => e.Lines).Where(l => l.Debit > 0).GroupBy(l => l.Account!.Name))
                    rows.Add(new CashBookRow(group.Last().Id, group.Max(e => e.CreatedAt), byAccount.Key == "Stock" ? "Voids returned to stock" :
                        byAccount.Key == "Stock loss" ? "Voided pours" : text, byAccount.Key, null, byAccount.Sum(l => l.Debit), group.Any(e => e.IsLate),
                        false, false, null, group.Count));
            }

        foreach (var e in entries.Where(e => e.SourceType is not (SalePosting.SourceType or StockSelling.SaleSource or StockSelling.VoidSource)))
        {
            Guid? documentId = e.SourceType == MoneyActions.LedgerSource && Guid.TryParse(e.SourceId?.Split(':')[0], out var d) ? d : null;
            var text = Text(e, documentId is { } doc && documents.TryGetValue(doc, out var document) ? MoneyActions.Title(document, categories, suppliers) : null, Shift(e.ShiftId));
            rows.AddRange(EntryRows(e, text, all, documentId));
        }
        return rows.OrderBy(r => r.At).ToList();
    }

    /// <summary>"Cash short · CAROL shift", "Delivery · KBL 4471", "Stock count variance"…</summary>
    private static string Text(JournalEntry e, string? document, string shift)
    {
        var reversed = e.ReversesEntryId != null;
        var text = document ?? e.SourceType switch
        {
            ShiftPosting.SourceType when e.Memo.StartsWith("Pay out", StringComparison.Ordinal) =>
                $"{e.Memo["Pay out · ".Length..]} (pay out){shift}",
            ShiftPosting.SourceType when e.Memo == "Cash drop" => $"Cash drop{shift}",
            ShiftPosting.SourceType when e.Memo == "Opening float" => $"Float{shift}",
            ShiftPosting.SourceType => $"Cash-up{shift}",
            StockCountCommands.LedgerSource => e.Memo.StartsWith("Opening", StringComparison.Ordinal) ? "Opening stock" : "Stock count variance",
            _ => e.Memo,
        };
        return reversed && !text.StartsWith("Reversed", StringComparison.Ordinal) ? $"Reversed: {text}" : text;
    }

    /// <summary>
    /// The rows for one entry. Money moved between two of our own places is one row ("Safe → Bank"),
    /// a cash-up is the difference then the drawer to the safe, and anything else is its money in or out.
    /// </summary>
    private static IEnumerable<CashBookRow> EntryRows(JournalEntry e, string text, bool all, Guid? documentId)
    {
        CashBookRow Row(string t, string account, decimal? @in, decimal? @out) =>
            new(e.Id, e.CreatedAt, t, account, @in is 0 ? null : @in, @out is 0 ? null : @out, e.IsLate, e.ReversedByEntryId != null,
                e.ReversesEntryId != null, documentId, 1);
        string Name(JournalLine l) => l.Account!.Code == LedgerAccountCodes.Cash ? "drawer" : l.Account.Name;

        var money = e.Lines.Where(l => LedgerAccountCodes.Money.Contains(l.Account!.Code)).ToList();
        var other = e.Lines.Except(money).ToList();
        var shortOrOver = other.Where(l => l.Account!.Code == LedgerAccountCodes.CashOverShort).ToList();
        if (shortOrOver.Count > 0)
        {
            var amount = shortOrOver.Sum(l => l.Debit - l.Credit);
            yield return Row(amount > 0 ? text.Replace("Cash-up", "Cash short") : text.Replace("Cash-up", "Cash over"), "Cash over/short",
                amount < 0 ? -amount : null, amount > 0 ? amount : null);
            other = other.Except(shortOrOver).ToList();
            money = money.Where(l => !shortOrOver.Any(s => s.Debit == l.Credit && s.Credit == l.Debit && l.Account!.Code == LedgerAccountCodes.Cash)).ToList();
            text = text.Replace("Cash-up", "Drawer to the safe");
        }

        var ins = money.Where(l => l.Debit > 0).ToList();
        var outs = money.Where(l => l.Credit > 0).ToList();
        if (ins.Count > 0 && outs.Count > 0)
        {
            var moved = ins.Sum(l => l.Debit);
            yield return Row(text, $"{Name(outs[0])} → {Name(ins[0])}", null, moved);
            var fees = other.Where(l => l.Debit > 0).ToList();
            foreach (var fee in fees) yield return Row($"{fee.Account!.Name} · {text}", fee.Account.Name, null, fee.Debit);
            yield break;
        }
        if (money.Count > 0)
        {
            var counter = other.FirstOrDefault()?.Account!.Name;
            var place = string.Join(", ", money.Select(Name).Distinct());
            yield return Row(text, counter is null ? place : $"{counter} · {place}", ins.Sum(l => l.Debit), outs.Sum(l => l.Credit));
            yield break;
        }
        if (!all) yield break;
        var main = e.Lines.FirstOrDefault(l => l.Account!.Type is AccountType.Expense or AccountType.Income) ?? e.Lines.First(l => l.Debit > 0);
        var value = main.Debit - main.Credit;
        yield return Row(text, main.Account!.Name, value < 0 ? -value : null, value > 0 ? value : null);
    }

    public async Task<EntryDetailDto> Entry(Guid id, CancellationToken ct)
    {
        await access.RequireView(session.RequireUser(), ct);
        var e = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).ThenInclude(l => l.Account).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Entry not found.");
        var categories = await db.ExpenseCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var by = e.CreatedByUserId is { } u ? await db.Users.Where(x => x.Id == u).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        MoneyDocumentDto? document = null;
        if (e.SourceType == MoneyActions.LedgerSource && Guid.TryParse(e.SourceId?.Split(':')[0], out var d))
            document = (await money.View([d], ct)).SingleOrDefault();
        return new EntryDetailDto(e.Id, e.BusinessDate, e.CreatedAt, e.Memo, by, e.IsLate, e.ReversesEntryId, e.ReversedByEntryId,
            e.Lines.Select(l => new EntryLineDto(l.Account!.Name, l.Account.Code, l.Debit, l.Credit, l.Memo,
                l.ExpenseCategoryId is { } c ? categories.GetValueOrDefault(c) : null)).ToList(), document);
    }

    /// <summary>The day's rows as CSV: Date, Time, Entry, Account, In, Out, Late, Reversed.</summary>
    public async Task<string> Csv(DateOnly date, bool all, CancellationToken ct)
    {
        var book = await Day(date, all, ct);
        var zone = await BusinessDates.Zone(db, ct);
        var csv = new StringBuilder("Date,Time,Entry,Account,In,Out,Late,Reversed\r\n");
        static string Cell(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        static string Money(decimal? m) => m?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        foreach (var r in book.Rows)
            csv.Append(CultureInfo.InvariantCulture,
                $"{date:yyyy-MM-dd},{TimeZoneInfo.ConvertTime(r.At, zone):HH:mm},{Cell(r.Text)},{Cell(r.Account)},{Money(r.In)},{Money(r.Out)},{(r.Late ? "yes" : "")},{(r.Reversed ? "yes" : "")}\r\n");
        return csv.ToString();
    }
}

/// <summary>The daily summary, worked out from the journal lines of one business date.</summary>
public static class Summaries
{
    /// <param name="includeLate">False for the day report printed at the close: late entries made after it aren't on it.</param>
    public static async Task<DailySummaryDto> For(ITausiDb db, DateOnly date, bool includeLate, CancellationToken ct)
    {
        var lines = await db.JournalLines.AsNoTracking()
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id,
                (l, e) => new { Code = l.Account!.Code, l.Debit, l.Credit, l.ExpenseCategoryId, l.Memo, e.BusinessDate, e.IsLate, e.SourceType })
            .Where(x => x.BusinessDate == date && (includeLate || !x.IsLate)).ToListAsync(ct);
        decimal Dr(string code) => lines.Where(l => l.Code == code).Sum(l => l.Debit - l.Credit);
        decimal Cr(string code) => -Dr(code);

        var vat = Cr(LedgerAccountCodes.VatPayable);
        var rounding = Cr(LedgerAccountCodes.Rounding);
        var net = Cr(LedgerAccountCodes.Sales) + rounding;
        var cost = Dr(LedgerAccountCodes.CostOfSales);
        var gross = net - cost;
        var categories = await db.ExpenseCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var expenseLines = lines.Where(l => l.Code == LedgerAccountCodes.Expenses).ToList();
        var byCategory = expenseLines
            .GroupBy(l => l.ExpenseCategoryId is { } c ? categories.GetValueOrDefault(c, l.Memo) : string.IsNullOrEmpty(l.Memo) ? "Other" : l.Memo)
            .Select(g => new NamedAmount(g.Key, g.Sum(l => l.Debit - l.Credit))).Where(x => x.Amount != 0).OrderByDescending(x => x.Amount).ToList();
        var expenses = expenseLines.Sum(l => l.Debit - l.Credit);
        var lossParts = lines.Where(l => l.Code == LedgerAccountCodes.StockLoss)
            .GroupBy(l => l.SourceType switch
            {
                WasteAndTransferCommands.WasteSource => "Waste",
                StockSelling.VoidSource => "Poured voids",
                StockCountCommands.LedgerSource => "Count variance",
                _ => "Other stock loss",
            })
            .Select(g => new NamedAmount(g.Key, g.Sum(l => l.Debit - l.Credit)))
            .Append(new NamedAmount("Cash over/short", Dr(LedgerAccountCodes.CashOverShort)))
            .Where(x => x.Amount != 0).ToList();
        var loss = lossParts.Sum(x => x.Amount);
        var charges = Dr(LedgerAccountCodes.BankCharges);
        var other = Cr(LedgerAccountCodes.OtherIncome);

        return new DailySummaryDto(date, net - rounding + vat, vat, net, cost, gross, net == 0 ? null : decimal.Round(gross / net * 100m, 1),
            expenses, byCategory, loss, lossParts, charges, other, gross - expenses - loss - charges + other, await NoRecipe(db, date, ct), rounding);
    }

    /// <summary>Menu items sold that day with no recipe: their cost of sales is missing.</summary>
    public static async Task<IReadOnlyList<string>> NoRecipe(ITausiDb db, DateOnly date, CancellationToken ct)
    {
        var periods = await BusinessDates.PeriodsOn(db, date, ct);
        var withRecipe = db.RecipeLines.Select(r => r.MenuItemId);
        return await db.OrderLines.AsNoTracking()
            .Where(l => l.Status == OrderLineStatus.Printed && l.MenuItemId != null && !withRecipe.Contains(l.MenuItemId.Value))
            .Join(db.Tickets.Where(k => periods.Contains(k.WorkPeriodId)), l => l.TicketId, k => k.Id, (l, k) => l.Name)
            .Distinct().OrderBy(n => n).ToListAsync(ct);
    }
}
