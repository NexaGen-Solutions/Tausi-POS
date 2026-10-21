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
}
