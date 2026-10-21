using Tausi.Domain.Common;

namespace Tausi.Domain.Menu;

public static class PriceChangeSources
{
    public const string Admin = "admin";
    public const string Import = "import";
    public const string Ticket = "ticket";
}

/// <summary>Menu: a price on a price list (admin site, import). Ticket: one line's price on one ticket.</summary>
public enum PriceChangeKind
{
    Menu,
    Ticket,
}

/// <summary>One price change (on the menu, or on a ticket line): old, new, who, approved by whom, where and when.</summary>
public class PriceChangeLog : Entity
{
    public PriceChangeKind Kind { get; set; } = PriceChangeKind.Menu;

    /// <summary>For a ticket price change: the ticket and line.</summary>
    public Guid? TicketId { get; set; }
    public Guid? OrderLineId { get; set; }
    public string? Reason { get; set; }

    /// <summary>The item; null for a modifier option's add-on.</summary>
    public Guid? MenuItemId { get; set; }

    /// <summary>The price list; null for a modifier option's add-on.</summary>
    public Guid? PriceListId { get; set; }

    /// <summary>Set for a modifier option's add-on instead of an item price.</summary>
    public Guid? ModifierOptionId { get; set; }

    /// <summary>Null when the item had no price on that list before.</summary>
    public decimal? OldPrice { get; set; }

    /// <summary>Null when the price was removed from a (non-default) list.</summary>
    public decimal? NewPrice { get; set; }

    public Guid ChangedByUserId { get; set; }
    public Guid? ApprovedByUserId { get; set; }

    /// <summary>The terminal it was changed on; null means the admin site.</summary>
    public Guid? TerminalId { get; set; }

    public string Source { get; set; } = PriceChangeSources.Admin;
    public DateTimeOffset At { get; set; }
}
