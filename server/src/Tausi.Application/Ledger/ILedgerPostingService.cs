using Tausi.Domain.Ledger;

namespace Tausi.Application.Ledger;

/// <summary>The only way journal entries are written. Rejects unbalanced entries.</summary>
public interface ILedgerPostingService
{
    /// <summary>
    /// Validates the draft and adds the entry to the current unit of work; it is saved with the
    /// caller's <c>SaveChangesAsync</c>. Throws <see cref="UnbalancedEntryException"/>.
    /// </summary>
    Task<JournalEntry> Post(JournalEntryDraft draft, CancellationToken ct = default);
}
