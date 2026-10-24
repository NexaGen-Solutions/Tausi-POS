using Tausi.Domain.Common;

namespace Tausi.Domain.Money;

public enum MoneyDocumentKind
{
    Expense,
    SupplierPayment,
    MoveMoney,
    OwnerDrawing,
    OwnerCapital,
    OtherIncome,
    OpeningBalances,
}

/// <summary>Where money is kept, for the admin site's money actions (the drawers have their own: Pay out).</summary>
public enum MoneyAccount
{
    Safe,
    Bank,
    Mpesa,
}

public enum MoneyDocumentStatus
{
    Pending,
    Posted,
    Rejected,
}

/// <summary>One opening balance: a money account's starting amount, or what a supplier was owed.</summary>
public sealed record OpeningLine(MoneyAccount? Account, Guid? SupplierId, decimal Amount);

/// <summary>
/// A money action on the admin site: an expense, a supplier payment, moving money between the safe,
/// bank and M-Pesa, the owner's money, other income, or the opening balances. It posts one balanced
/// journal entry (or waits as Pending when the key needs approval). It is never edited: a mistake is
/// put right by reversing it, which posts the exact opposite.
/// </summary>
public class MoneyDocument : VersionedEntity
{
    public const int MaxTextLength = 120;
    public const int MaxNoteLength = 300;

    public MoneyDocumentKind Kind { get; set; }
    public MoneyDocumentStatus Status { get; set; }
    public DateOnly BusinessDate { get; set; }
    public decimal Amount { get; set; }
    public decimal Fee { get; set; }
    public MoneyAccount? From { get; set; }
    public MoneyAccount? To { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? SupplierId { get; set; }
    public string? Payee { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public Guid? AttachmentFileId { get; set; }

    /// <summary>A supplier payment can name the deliveries it pays for.</summary>
    public List<Guid> DeliveryIds { get; set; } = [];

    public List<OpeningLine> OpeningLines { get; set; } = [];

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public string? RejectNote { get; set; }
    public Guid? JournalEntryId { get; set; }

    /// <summary>A reversal: Pending while it waits for approval, Posted once the opposite entry is in.</summary>
    public MoneyDocumentStatus? ReversalStatus { get; set; }
    public string? ReversalReason { get; set; }
    public Guid? ReversalRequestedByUserId { get; set; }
    public Guid? ReversalApprovedByUserId { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
    public Guid? ReversalEntryId { get; set; }

    /// <summary>Counts in the books and balances: posted and not reversed.</summary>
    public bool IsStanding => Status == MoneyDocumentStatus.Posted && ReversalStatus != MoneyDocumentStatus.Posted;
}
