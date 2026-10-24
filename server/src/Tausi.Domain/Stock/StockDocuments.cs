using Tausi.Domain.Common;

namespace Tausi.Domain.Stock;

/// <summary>
/// A delivery, waste record, transfer or reversal. With the permission "Needs approval" it waits as
/// Pending until someone allowed (not the creator) approves it; only then does stock move.
/// </summary>
public enum StockDocumentStatus
{
    Pending,
    Posted,
    Rejected,
}

public enum DeliveryPayment
{
    OnCredit,
    Cash,
    Mpesa,
    Bank,
}

/// <summary>A supplier's delivery into a location, paid now or owed.</summary>
public class Delivery : VersionedEntity
{
    public const int MaxInvoiceLength = 40;
    public const int MaxReferenceLength = 60;
    public const int MaxNoteLength = 300;

    public Guid SupplierId { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public DateOnly Date { get; set; }
    public Guid LocationId { get; set; }
    public DeliveryPayment Payment { get; set; }
    public string? PaymentReference { get; set; }
    public decimal Total { get; set; }
    public StockDocumentStatus Status { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public string? RejectNote { get; set; }

    /// <summary>A reversal: Pending while it waits for approval, Posted once the stock went back.</summary>
    public StockDocumentStatus? ReversalStatus { get; set; }
    public string? ReversalReason { get; set; }
    public Guid? ReversalRequestedByUserId { get; set; }
    public DateTimeOffset? ReversalRequestedAt { get; set; }
    public Guid? ReversalApprovedByUserId { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }

    public List<DeliveryLine> Lines { get; set; } = [];

    /// <summary>Counts towards stock and what is owed: posted and not reversed.</summary>
    public bool IsStanding => Status == StockDocumentStatus.Posted && ReversalStatus != StockDocumentStatus.Posted;
}

/// <summary>"4 crates of Tusker at 2,500 a crate": the base quantity and unit cost are worked out from the pack.</summary>
public class DeliveryLine : Entity
{
    public Guid DeliveryId { get; set; }
    public Guid StockItemId { get; set; }
    public string Unit { get; set; } = "";
    public decimal Factor { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }
    public decimal BaseQuantity => Quantity * Factor;
    public decimal BaseUnitCost => Factor == 0 ? 0 : UnitCost / Factor;
}

public static class WasteReasons
{
    public const string Broken = "Broken";
    public const string Expired = "Expired";
    public const string Spilled = "Spilled";
    public const string StaffUse = "Staff use";
    public const string Other = "Other";
    public static readonly IReadOnlyList<string> All = [Broken, Expired, Spilled, StaffUse, Other];
}

/// <summary>Stock thrown away or used up without a sale: Stock loss in the books.</summary>
public class WasteRecord : VersionedEntity
{
    public const int MaxNoteLength = 200;

    public Guid LocationId { get; set; }
    public Guid StockItemId { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "";
    public decimal BaseQuantity { get; set; }
    public string Reason { get; set; } = "";
    public string? Note { get; set; }
    public decimal Cost { get; set; }
    public StockDocumentStatus Status { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public string? RejectNote { get; set; }
}

/// <summary>Stock moved between locations (Store → Bar). The total and its value don't change.</summary>
public class StockTransfer : VersionedEntity
{
    public Guid FromLocationId { get; set; }
    public Guid ToLocationId { get; set; }
    public StockDocumentStatus Status { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public string? RejectNote { get; set; }
    public List<StockTransferLine> Lines { get; set; } = [];
}

public class StockTransferLine : Entity
{
    public Guid TransferId { get; set; }
    public Guid StockItemId { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "";
    public decimal BaseQuantity { get; set; }
}

public enum StockCountKind
{
    Full,
    Partial,
}

public enum StockCountStatus
{
    Draft,
    Submitted,
    Approved,
    Rejected,
}

/// <summary>
/// A count of one location, as at the moment it was started: the expected figure is what the
/// movements say was there then, so sales during the count are kept. The counter doesn't see the
/// expected figures until they submit (a blind count). An opening count has no expected figure and
/// sets each item's cost.
/// </summary>
public class StockCount : VersionedEntity
{
    public const int MaxNoteLength = 300;

    public Guid LocationId { get; set; }
    public StockCountKind Kind { get; set; }
    public bool IsOpening { get; set; }
    public StockCountStatus Status { get; set; }
    public DateTimeOffset AsAt { get; set; }
    public Guid CountedByUserId { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? RejectNote { get; set; }
    public decimal? VarianceValue { get; set; }
    public List<StockCountLine> Lines { get; set; } = [];
}

public class StockCountLine : Entity
{
    public Guid CountId { get; set; }
    public Guid StockItemId { get; set; }

    /// <summary>What was typed, per unit: {"crate":3,"bottle":7}.</summary>
    public Dictionary<string, decimal> Entered { get; set; } = [];

    /// <summary>The total in base units; null until counted.</summary>
    public decimal? Counted { get; set; }

    /// <summary>Set when the count is submitted (as at <see cref="StockCount.AsAt"/>).</summary>
    public decimal? Expected { get; set; }

    /// <summary>The cost per base unit: typed for an opening count, else the average cost at approval.</summary>
    public decimal? UnitCost { get; set; }

    public decimal? Difference => Counted is { } c && Expected is { } e ? c - e : null;
}
