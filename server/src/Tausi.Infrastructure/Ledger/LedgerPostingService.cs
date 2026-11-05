using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Ledger;
using Tausi.Application.Money;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;

namespace Tausi.Infrastructure.Ledger;

public sealed class LedgerPostingService(ITausiDb db, ICurrentSession session, TimeProvider clock) : ILedgerPostingService
{
    public async Task<JournalEntry> Post(JournalEntryDraft draft, CancellationToken ct = default)
    {
        draft.EnsureBalanced();
        // Training sales never reach the books: the entry is checked and handed back, not saved.
        if (db.TrainingMode) return new JournalEntry { Date = draft.Date, Memo = draft.Memo, SourceType = draft.SourceType, SourceId = draft.SourceId };

        var codes = draft.Lines.Select(l => l.AccountCode).Distinct().ToList();
        var accounts = await db.LedgerAccounts
            .Where(a => codes.Contains(a.Code) && a.Active)
            .ToDictionaryAsync(a => a.Code, ct);
        var missing = codes.Where(c => !accounts.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            throw new DomainException($"Unknown or inactive ledger account: {string.Join(", ", missing)}.");

        // The business date: as chosen (admin forms and sales served on another day), else the calendar
        // date it happened, whichever work period is open.
        var date = draft.BusinessDate ?? BusinessDates.Of(draft.Date, await BusinessDates.Zone(db, ct));
        var late = await BusinessDates.IsClosed(db, date, ct);

        var entry = new JournalEntry
        {
            Date = draft.Date,
            Memo = draft.Memo,
            SourceType = draft.SourceType,
            SourceId = draft.SourceId,
            CreatedAt = clock.GetUtcNow(),
            CreatedByUserId = session.User?.UserId,
            WorkPeriodId = draft.WorkPeriodId,
            ShiftId = draft.ShiftId,
            BusinessDate = date,
            IsLate = late,
            ReversesEntryId = draft.ReversesEntryId,
            Lines = draft.Lines.Select(l => new JournalLine
            {
                AccountId = accounts[l.AccountCode].Id,
                Debit = l.Debit,
                Credit = l.Credit,
                Memo = l.Memo,
                ExpenseCategoryId = l.ExpenseCategoryId,
                SourceType = draft.SourceType,
                SourceId = draft.SourceId,
            }).ToList(),
        };
        db.JournalEntries.Add(entry);
        if (draft.ReversesEntryId is { } reversed)
            await db.JournalEntries.Where(e => e.Id == reversed).ExecuteUpdateAsync(u => u.SetProperty(e => e.ReversedByEntryId, entry.Id), ct);
        return entry;
    }
}
