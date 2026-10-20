using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

/// <summary>
/// One line on a ticket. Who added it and on which terminal come from the session and are
/// required. Amounts are stored as gross (VAT included), net and VAT; a line is never edited
/// in place, only voided.
/// </summary>
public class OrderLine : Entity
{
    public Guid TicketId { get; set; }
    public Guid? MenuItemId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Chosen options, e.g. "Cold".</summary>
    public string? Options { get; set; }
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

    public DateTimeOffset? VoidedAt { get; set; }
    public Guid? VoidedByUserId { get; set; }

    public bool IsVoided => VoidedAt is not null;
}
