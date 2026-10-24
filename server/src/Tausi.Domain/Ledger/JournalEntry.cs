using Tausi.Domain.Common;

namespace Tausi.Domain.Ledger;

public class JournalEntry : Entity
{
    public DateTimeOffset Date { get; set; }
    public string Memo { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string? SourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    /// <summary>The business day the entry belongs to (set for sales).</summary>
    public Guid? WorkPeriodId { get; set; }
    /// <summary>The cashier's shift, for drawer entries (float, pay-outs, drops, close) and sales.</summary>
    public Guid? ShiftId { get; set; }

    /// <summary>
    /// The business day it belongs to: the work period's opening date (a Friday that runs past
    /// midnight is still Friday), or the date chosen on an admin form. Null only before the back-fill.
    /// </summary>
    public DateOnly? BusinessDate { get; set; }

    /// <summary>Back-dated into a day that was already closed: shown as "Late entry", not on that day's Z.</summary>
    public bool IsLate { get; set; }

    /// <summary>This entry reverses that one (its exact opposite); both stay in the books.</summary>
    public Guid? ReversesEntryId { get; set; }

    /// <summary>The entry that reversed this one.</summary>
    public Guid? ReversedByEntryId { get; set; }
    public List<JournalLine> Lines { get; set; } = [];
}

public class JournalLine : Entity
{
    public Guid JournalEntryId { get; set; }
    public Guid AccountId { get; set; }
    public LedgerAccount? Account { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Memo { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string? SourceId { get; set; }

    /// <summary>The expense category of an Expenses line (a dimension for reports).</summary>
    public Guid? ExpenseCategoryId { get; set; }
}
