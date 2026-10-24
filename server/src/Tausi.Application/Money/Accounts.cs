using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.Ledger;
using Tausi.Domain.People;

namespace Tausi.Application.Money;

/// <summary>
/// An account's balance in the way people read it: positive for money, stock and costs; positive for
/// what is owed, income and the owner's stake (drawings reduce it, so they show below 0).
/// </summary>
public sealed record AccountBalanceDto(string Code, string Name, string Group, decimal Balance);

public sealed record AccountGroupDto(string Name, IReadOnlyList<AccountBalanceDto> Accounts, decimal Total);

/// <summary>Debits = credits over every entry up to the date; <c>Difference</c> is 0 when the books balance.</summary>
public sealed record TrialBalanceDto(decimal Debits, decimal Credits, decimal Difference);

public sealed record AccountsDto(DateOnly AsAt, TrialBalanceDto Trial, IReadOnlyList<AccountGroupDto> Groups);

public sealed record LedgerLineDto(
    Guid EntryId, DateOnly? Date, DateTimeOffset At, string Entry, string Memo, decimal Debit, decimal Credit, decimal Balance, bool Late);

public sealed record AccountLedgerDto(string Code, string Name, DateOnly From, DateOnly To, decimal Opening, IReadOnlyList<LedgerLineDto> Lines, decimal Closing);

/// <summary>The books behind the cash book, read-only (cash.book): balances by group, each account's ledger, and the trial balance.</summary>
public sealed class Accounts(ITausiDb db, ICurrentSession session, IPermissionService permissions)
{
    private static readonly (string Group, string[] Codes)[] Groups =
    [
        ("Money", [LedgerAccountCodes.Cash, LedgerAccountCodes.Safe, LedgerAccountCodes.Bank, LedgerAccountCodes.MPesa, LedgerAccountCodes.CardClearing]),
        ("Stock", [LedgerAccountCodes.Stock]),
        ("Owed", [LedgerAccountCodes.SupplierPayables, LedgerAccountCodes.VatPayable]),
        ("Sales and income", [LedgerAccountCodes.Sales, LedgerAccountCodes.OtherIncome, LedgerAccountCodes.Rounding]),
        ("Costs and expenses", [LedgerAccountCodes.CostOfSales, LedgerAccountCodes.StockLoss, LedgerAccountCodes.Expenses, LedgerAccountCodes.BankCharges, LedgerAccountCodes.CashOverShort]),
        ("Owner", [LedgerAccountCodes.OwnersCapital, LedgerAccountCodes.OwnersDrawings, LedgerAccountCodes.OpeningBalances]),
    ];

    /// <summary>Groups whose balances read as credit − debit.</summary>
    private static readonly HashSet<string> CreditGroups = ["Owed", "Sales and income", "Owner"];

    public async Task<AccountsDto> Balances(DateOnly asAt, CancellationToken ct)
    {
        await Require(ct);
        var accounts = await db.LedgerAccounts.AsNoTracking().ToListAsync(ct);
        var balances = await CashBook.Balances(db, asAt, ct);
        var groups = new List<AccountGroupDto>();
        var known = new HashSet<string>();
        foreach (var (group, codes) in Groups.Append(("Other", accounts.Select(a => a.Code).Where(c => Groups.All(g => !g.Codes.Contains(c))).ToArray())))
        {
            var sign = CreditGroups.Contains(group) ? -1 : 1;
            var rows = codes.Select(code => accounts.FirstOrDefault(a => a.Code == code)).OfType<LedgerAccount>()
                .Where(a => known.Add(a.Code))
                .Select(a => new AccountBalanceDto(a.Code, a.Name, group, sign * balances.GetValueOrDefault(a.Code))).ToList();
            if (rows.Count > 0) groups.Add(new AccountGroupDto(group, rows, rows.Sum(r => r.Balance)));
        }
        return new AccountsDto(asAt, await Trial(db, asAt, ct), groups);
    }

    public static async Task<TrialBalanceDto> Trial(ITausiDb db, DateOnly asAt, CancellationToken ct)
    {
        var totals = await db.JournalLines.AsNoTracking()
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l.Debit, l.Credit, e.BusinessDate })
            .Where(x => x.BusinessDate <= asAt)
            .GroupBy(_ => 1).Select(g => new { Debits = g.Sum(x => x.Debit), Credits = g.Sum(x => x.Credit) })
            .FirstOrDefaultAsync(ct);
        var debits = totals?.Debits ?? 0;
        var credits = totals?.Credits ?? 0;
        return new TrialBalanceDto(debits, credits, debits - credits);
    }

    /// <summary>An account's lines in a date range with the running balance (read the same way as on the balances).</summary>
    public async Task<AccountLedgerDto> Ledger(string code, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await Require(ct);
        var account = await db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Code == code, ct) ?? throw new NotFoundException("Account not found.");
        var group = Groups.FirstOrDefault(g => g.Codes.Contains(code)).Group ?? "Other";
        var sign = CreditGroups.Contains(group) ? -1 : 1;
        var rows = await db.JournalLines.AsNoTracking().Where(l => l.AccountId == account.Id)
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id,
                (l, e) => new { e.Id, e.BusinessDate, e.CreatedAt, e.Memo, LineMemo = l.Memo, l.Debit, l.Credit, e.IsLate })
            .Where(x => x.BusinessDate <= to).ToListAsync(ct);
        var opening = sign * rows.Where(r => r.BusinessDate < from).Sum(r => r.Debit - r.Credit);
        var running = opening;
        var lines = new List<LedgerLineDto>();
        foreach (var r in rows.Where(r => r.BusinessDate >= from).OrderBy(r => r.BusinessDate).ThenBy(r => r.CreatedAt))
        {
            running += sign * (r.Debit - r.Credit);
            lines.Add(new LedgerLineDto(r.Id, r.BusinessDate, r.CreatedAt, r.Memo, r.LineMemo, r.Debit, r.Credit, running, r.IsLate));
        }
        return new AccountLedgerDto(account.Code, account.Name, from, to, opening, lines, running);
    }

    private async Task Require(CancellationToken ct)
    {
        if (await permissions.Check(session.RequireUser(), PermissionKeys.CashBook, ct) == PermissionValue.NotAllowed)
            throw new PermissionDeniedException(PermissionKeys.CashBook);
    }
}
