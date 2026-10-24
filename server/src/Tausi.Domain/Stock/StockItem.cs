using System.Globalization;
using Tausi.Domain.Common;

namespace Tausi.Domain.Stock;

/// <summary>Where stock is kept: the demo has a Store and a Bar.</summary>
public class StockLocation : VersionedEntity
{
    public const int MaxNameLength = 60;
    public const string Store = "Store";
    public const string Bar = "Bar";

    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public int SortOrder { get; set; }
}

public enum StockCategory
{
    Beer,
    Spirits,
    SoftDrinks,
    Food,
    Other,
}

/// <summary>The unit quantities are kept in. Packs are counted in these.</summary>
public enum StockUnit
{
    Bottle,
    Can,
    Piece,
    Ml,
    G,
}

/// <summary>A pack of the item, e.g. "crate" = 25 bottles, or "bottle" = 375 ml.</summary>
public sealed record StockPack(string Name, decimal Factor);

/// <summary>
/// Something the business buys and sells: Tusker (bottles, crates of 25), Viceroy (ml, bottles of
/// 375 and cartons of 9,000). Quantities are always in the base unit; the average cost is per base
/// unit and business-wide (all locations).
/// </summary>
public class StockItem : VersionedEntity
{
    public const int MaxNameLength = 80;
    public const int MaxPackNameLength = 30;

    public string Name { get; set; } = "";
    public StockCategory Category { get; set; }
    public StockUnit BaseUnit { get; set; }

    /// <summary>The packs, as JSON: [{"Name":"crate","Factor":25}].</summary>
    public List<StockPack> Packs { get; set; } = [];

    public decimal AverageCost { get; set; }
    public bool Active { get; set; } = true;

    public static string UnitName(StockUnit unit) => unit switch
    {
        StockUnit.Bottle => "bottle",
        StockUnit.Can => "can",
        StockUnit.Piece => "piece",
        StockUnit.Ml => "ml",
        _ => "g",
    };

    public string BaseUnitName => UnitName(BaseUnit);

    /// <summary>The factor of a unit of this item in base units: 1 for the base unit, else its pack.</summary>
    public decimal FactorOf(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit) || string.Equals(unit.Trim(), BaseUnitName, StringComparison.OrdinalIgnoreCase)) return 1m;
        return Packs.FirstOrDefault(p => string.Equals(p.Name, unit.Trim(), StringComparison.OrdinalIgnoreCase))?.Factor
            ?? throw new DomainException($"{Name} has no unit \"{unit}\". Use {string.Join(", ", UnitNames)}.", "unknown_unit");
    }

    /// <summary>The base unit, then the packs from smallest to largest.</summary>
    public IEnumerable<string> UnitNames => new[] { BaseUnitName }.Concat(Packs.OrderBy(p => p.Factor).Select(p => p.Name));

    /// <summary>The largest pack, or the base unit: what deliveries come in by default.</summary>
    public string LargestUnit => Packs.OrderByDescending(p => p.Factor).FirstOrDefault()?.Name ?? BaseUnitName;

    /// <summary>
    /// A quantity in the clearest packs: "3 crates 7 bottles", or for a liquid "2.4 bottles (900 ml)".
    /// Negative quantities keep their sign.
    /// </summary>
    public string Describe(decimal quantity) => DescribeQuantity(quantity, BaseUnit, Packs);

    public static string DescribeQuantity(decimal quantity, StockUnit baseUnit, IReadOnlyCollection<StockPack> packs)
    {
        var unit = UnitName(baseUnit);
        var sign = quantity < 0 ? "-" : "";
        var left = Math.Abs(quantity);
        var smallestPack = packs.Where(p => p.Factor > 1).OrderBy(p => p.Factor).FirstOrDefault();
        if (baseUnit is StockUnit.Ml or StockUnit.G)
        {
            // Liquids and weights read best in their container: "2.4 bottles (900 ml)".
            if (smallestPack is null || left < smallestPack.Factor / 10) return $"{sign}{Number(left)} {unit}";
            var containers = decimal.Round(left / smallestPack.Factor, 1);
            return $"{sign}{Number(containers)} {Plural(smallestPack.Name, containers)} ({Number(left)} {unit})";
        }

        var parts = new List<string>();
        foreach (var pack in packs.Where(p => p.Factor > 1).OrderByDescending(p => p.Factor))
        {
            var whole = decimal.Floor(left / pack.Factor);
            if (whole == 0) continue;
            parts.Add($"{Number(whole)} {Plural(pack.Name, whole)}");
            left -= whole * pack.Factor;
        }
        if (left != 0 || parts.Count == 0) parts.Add($"{Number(left)} {Plural(unit, left)}");
        return sign + string.Join(" ", parts);
    }

    private static string Number(decimal n) => n.ToString("#,##0.##", CultureInfo.InvariantCulture);

    private static string Plural(string name, decimal n) =>
        n == 1 || name.EndsWith('s') || name is "ml" or "g" ? name : name + "s";
}

/// <summary>
/// The cached on-hand quantity of an item at a location, kept in step with the movements in the
/// same transaction (it always equals their sum), and the location's optional reorder level.
/// </summary>
public class StockLevel : Entity
{
    public Guid StockItemId { get; set; }
    public Guid LocationId { get; set; }
    public decimal OnHand { get; set; }
    public decimal? ReorderLevel { get; set; }
}

public enum StockMovementKind
{
    Opening,
    Delivery,
    DeliveryReversal,
    Sale,
    VoidReturn,
    Waste,
    TransferOut,
    TransferIn,
    CountAdjust,
}

/// <summary>
/// The only source of truth for quantities: + in, − out, in base units, with the unit cost at that
/// moment. The reference says what caused it (an order line, a delivery, a count…).
/// </summary>
public class StockMovement : Entity
{
    public Guid StockItemId { get; set; }
    public Guid LocationId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public StockMovementKind Kind { get; set; }
    public string ReferenceType { get; set; } = "";
    public Guid? ReferenceId { get; set; }

    /// <summary>For a sale: the order line's quantity when it was printed, so a later void returns its share.</summary>
    public decimal? LineQuantity { get; set; }

    public Guid? UserId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? WorkPeriodId { get; set; }

    public decimal Value => Quantity * UnitCost;
}

/// <summary>
/// One ingredient of a menu item's recipe: "25 ml of Viceroy 375ml". <see cref="BaseQuantity"/> is
/// the quantity in the stock item's base unit, worked out when the recipe is saved.
/// </summary>
public class RecipeLine : Entity
{
    public Guid MenuItemId { get; set; }
    public Guid StockItemId { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "";
    public decimal BaseQuantity { get; set; }
    public int SortOrder { get; set; }
}

public class Supplier : VersionedEntity
{
    public const int MaxNameLength = 80;

    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string KraPin { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Active { get; set; } = true;
}
