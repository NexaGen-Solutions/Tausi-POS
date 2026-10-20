using Nexagen.Domain.Common;

namespace Nexagen.Domain.Menu;

/// <summary>Something sold. Prices live in <see cref="MenuItemPrice"/>, one per price list.</summary>
public class MenuItem : VersionedEntity
{
    public const int MinNameLength = 2;
    public const int MaxNameLength = 60;
    public const int MaxShortNameLength = 18;

    public string Name { get; set; } = "";

    /// <summary>Shown on terminal buttons and printed slips; at most 18 characters.</summary>
    public string ShortName { get; set; } = "";

    public Guid CategoryId { get; set; }
    public Category? Category { get; set; }
    public int SortOrder { get; set; }

    /// <summary>The item's own VAT rate; null uses the business default; 0 for exempt items.</summary>
    public decimal? VatRate { get; set; }

    public bool Active { get; set; } = true;

    /// <summary>The item's code in ERPNext, for the optional sync.</summary>
    public string? ErpItemCode { get; set; }

    public List<MenuItemPrice> Prices { get; set; } = [];

    public static string DefaultShortName(string name)
    {
        var trimmed = name.Trim();
        return (trimmed.Length <= MaxShortNameLength ? trimmed : trimmed[..MaxShortNameLength]).TrimEnd();
    }

    public decimal EffectiveVatRate(decimal businessDefault) => VatRate ?? businessDefault;
}

/// <summary>An item's price (including VAT) on one price list.</summary>
public class MenuItemPrice : Entity
{
    public Guid MenuItemId { get; set; }
    public Guid PriceListId { get; set; }
    public PriceList? PriceList { get; set; }
    public decimal Price { get; set; }
}
