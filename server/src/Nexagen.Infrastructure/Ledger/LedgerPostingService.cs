using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Ledger;
using Nexagen.Application.Security;
using Nexagen.Domain.Common;
using Nexagen.Domain.Ledger;

namespace Nexagen.Infrastructure.Ledger;

public sealed class LedgerPostingService(INexagenDb db, ICurrentSession session, TimeProvider clock) : ILedgerPostingService
{
    public async Task<JournalEntry> Post(JournalEntryDraft draft, CancellationToken ct = default)
    {
        draft.EnsureBalanced();

        var codes = draft.Lines.Select(l => l.AccountCode).Distinct().ToList();
        var accounts = await db.LedgerAccounts
            .Where(a => codes.Contains(a.Code) && a.Active)
            .ToDictionaryAsync(a => a.Code, ct);
        var missing = codes.Where(c => !accounts.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            throw new DomainException($"Unknown or inactive ledger account: {string.Join(", ", missing)}.");

        var entry = new JournalEntry
        {
            Date = draft.Date,
            Memo = draft.Memo,
            SourceType = draft.SourceType,
            SourceId = draft.SourceId,
            CreatedAt = clock.GetUtcNow(),
            CreatedByUserId = session.User?.UserId,
            Lines = draft.Lines.Select(l => new JournalLine
            {
                AccountId = accounts[l.AccountCode].Id,
                Debit = l.Debit,
                Credit = l.Credit,
                Memo = l.Memo,
                SourceType = draft.SourceType,
                SourceId = draft.SourceId,
            }).ToList(),
        };
        db.JournalEntries.Add(entry);
        return entry;
    }
}
