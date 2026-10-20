using Tausi.Domain.Common;

namespace Tausi.Domain.Menu;

public static class PriceChangeSources
{
    public const string Admin = "admin";
    public const string Import = "import";
}

/// <summary>One menu price change: old, new, who, approved by whom, where and when.</summary>
public class PriceChangeLog : Entity
{
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
