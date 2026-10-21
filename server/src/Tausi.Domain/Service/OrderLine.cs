using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

/// <summary>
/// New: on the ticket, not yet sent to the bar (its quantity can still change, or it can be
/// cancelled). Printed: sent with a print round; only a void changes it then.
/// Cancelled: taken off before printing; kept for the audit trail, not shown or billed.
/// Voided: a printed line (or part of one) taken off with a void; shown struck through, not billed.
/// </summary>
public enum OrderLineStatus
{
    New,
    Printed,
    Cancelled,
    Voided,
}

/// <summary>
/// One line on a ticket. Who added it and on which terminal come from the session and are
/// required. Name, price and VAT rate are copied when it is added, so later menu changes don't
/// touch it.
/// <para>
/// Amounts: <see cref="Gross"/> is quantity x unit price (VAT included; 0 for a gift).
/// <see cref="Discount"/> is the line's share of the ticket discount. <see cref="Net"/> and
/// <see cref="Vat"/> are worked out on what is paid, Gross - Discount, so VAT is right after a
/// discount: Net + Vat = Gross - Discount. The ticket total is the sum of Gross - Discount.
/// </para>
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
    /// <summary>Copied from the item: exempt from VAT (printed as "VAT exempt"), not zero-rated.</summary>
    public bool VatExempt { get; set; }
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
    /// <summary>The void's reason, shown on the struck-through line.</summary>
    public string? VoidReason { get; set; }

    /// <summary>Given free: the unit price stays (for reports), the amounts are 0.</summary>
    public bool IsGift { get; set; }

    /// <summary>The unit price before a price change on the ticket; null if it was never changed.</summary>
    public decimal? OriginalUnitPrice { get; set; }

    /// <summary>This line's share of the ticket discount (VAT included).</summary>
    public decimal Discount { get; set; }

    /// <summary>The dispense printer that printed this line (its print round's printer), for a VOID slip.</summary>
    public Guid? DispensePrinterId { get; set; }

    public bool IsVoided => Status == OrderLineStatus.Voided || VoidedAt is not null;

    /// <summary>On the ticket and the bill: not cancelled and not voided.</summary>
    public bool Counts => Status != OrderLineStatus.Cancelled && !IsVoided;

    /// <summary>What the customer pays for this line: Gross - Discount.</summary>
    public decimal Payable => Gross - Discount;

    /// <summary>Can still be voided, gifted or have its price changed.</summary>
    public bool IsChangeable => Counts && !IsGift;

    /// <summary>Sets the quantity and the amounts from the copied unit price and VAT rate.</summary>
    public void SetQuantity(decimal quantity)
    {
        Quantity = quantity;
        Recalculate();
    }

    /// <summary>Gross from quantity x unit price (0 for a gift), then net and VAT on Gross - Discount.</summary>
    public void Recalculate()
    {
        Gross = IsGift ? 0m : Money.MoneyMath.Round(UnitPrice * Quantity);
        if (Discount > Gross) Discount = Gross;
        var paid = Gross - Discount;
        var vat = VatExempt ? 0m : Money.VatCalculator.VatIncluded(paid, VatRate);
        Vat = vat;
        Net = paid - vat;
    }

    /// <summary>
    /// Takes <paramref name="quantity"/> off this line into a new line with the same item, price,
    /// who added it, terminal, time and print round (for a partial void, gift or price change).
    /// The whole quantity returns this line itself.
    /// </summary>
    public OrderLine SplitOff(decimal quantity)
    {
        if (quantity <= 0 || quantity > Quantity) throw new DomainException($"Choose from 1 to {Quantity:0.##}.");
        if (quantity == Quantity) return this;
        var part = new OrderLine
        {
            TicketId = TicketId, MenuItemId = MenuItemId, Name = Name, ShortName = ShortName, Options = Options,
            OptionIds = OptionIds, Note = Note, Status = Status, PrintRound = PrintRound, UnitPrice = UnitPrice,
            VatRate = VatRate, VatExempt = VatExempt, IsGift = IsGift, OriginalUnitPrice = OriginalUnitPrice,
            AddedByUserId = AddedByUserId, AddedTerminalId = AddedTerminalId, AddedAt = AddedAt,
            PrintedAt = PrintedAt, DispensePrinterId = DispensePrinterId,
        };
        part.SetQuantity(quantity);
        SetQuantity(Quantity - quantity);
        return part;
    }
}
