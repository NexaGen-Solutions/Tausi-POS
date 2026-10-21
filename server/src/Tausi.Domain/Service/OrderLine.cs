using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

/// <summary>
/// New: on the ticket, not yet sent to the bar (its quantity can still change, or it can be
/// cancelled). Printed: sent with a print round; only a void (Task 7) changes it then.
/// Cancelled: taken off before printing; kept for the audit trail, not shown or billed.
/// </summary>
public enum OrderLineStatus
{
    New,
    Printed,
    Cancelled,
}

/// <summary>
/// One line on a ticket. Who added it and on which terminal come from the session and are
/// required. Name, price and VAT rate are copied when it is added, so later menu changes don't
/// touch it. Amounts are stored as gross (VAT included), net and VAT.
/// </summary>
public class OrderLine : Entity
{
    public const int MaxNoteLength = 200;

    public Guid TicketId { get; set; }
    public Guid? MenuItemId { get; set; }
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    /// <summary>Chosen options as text, e.g. "Cold".</summary>
    public string? Options { get; set; }
    /// <summary>The chosen option ids, sorted and comma-separated ("" for none), to tell lines apart.</summary>
    public string OptionIds { get; set; } = "";
    public string? Note { get; set; }
    public OrderLineStatus Status { get; set; } = OrderLineStatus.New;
    /// <summary>The ticket's print round that sent this line; null while New.</summary>
    public int? PrintRound { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
    public decimal Gross { get; set; }
    public decimal Net { get; set; }
    public decimal Vat { get; set; }

    public Guid AddedByUserId { get; set; }
    public Guid AddedTerminalId { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>When it went to the dispense printer; null while it is still new.</summary>
    public DateTimeOffset? PrintedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }
    public Guid? VoidedByUserId { get; set; }

    public bool IsVoided => VoidedAt is not null;

    /// <summary>On the ticket and the bill: not cancelled and not voided.</summary>
    public bool Counts => Status != OrderLineStatus.Cancelled && !IsVoided;

    /// <summary>Sets the quantity and the amounts from the copied unit price and VAT rate.</summary>
    public void SetQuantity(decimal quantity)
    {
        var amounts = Money.VatCalculator.ForLine(UnitPrice, quantity, VatRate);
        Quantity = quantity;
        Gross = amounts.Gross;
        Net = amounts.Net;
        Vat = amounts.Vat;
    }
}
