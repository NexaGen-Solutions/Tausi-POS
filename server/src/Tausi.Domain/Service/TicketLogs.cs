using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

/// <summary>
/// One void: what was taken off, why, who asked and who approved. <see cref="WasPoured"/> tells
/// stock (Task 11) whether the drink was used up ("Spilled or broken" is always yes).
/// </summary>
public class VoidLog : Entity
{
    public const int MaxReasonLength = 200;

    public Guid TicketId { get; set; }
    /// <summary>The voided line (the split-off part for a partial void).</summary>
    public Guid OrderLineId { get; set; }
    public Guid? MenuItemId { get; set; }
    public string ItemName { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public string? Note { get; set; }
    public bool WasPoured { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid ApprovedByUserId { get; set; }
    public Guid TerminalId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? ApprovalRequestId { get; set; }
}

/// <summary>One gift: the item keeps its price for reports; it counts as sold for stock.</summary>
public class GiftLog : Entity
{
    public Guid TicketId { get; set; }
    public Guid OrderLineId { get; set; }
    public Guid? MenuItemId { get; set; }
    public string ItemName { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>What it would have cost: quantity x the original price.</summary>
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public string? Note { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid ApprovedByUserId { get; set; }
    public Guid TerminalId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? ApprovalRequestId { get; set; }
}

/// <summary>A ticket discount set, changed or removed (Kind null).</summary>
public class DiscountLog : Entity
{
    public Guid TicketId { get; set; }
    public DiscountKind? Kind { get; set; }
    public decimal Value { get; set; }
    /// <summary>The discount in money when it was set.</summary>
    public decimal Amount { get; set; }
    public DiscountKind? OldKind { get; set; }
    public decimal OldValue { get; set; }
    public string? Reason { get; set; }
    public string? Note { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid ApprovedByUserId { get; set; }
    public Guid TerminalId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? ApprovalRequestId { get; set; }
}
