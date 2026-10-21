using Tausi.Domain.Common;

namespace Tausi.Domain.Menu;

/// <summary>A question asked when an item is added, e.g. Temperature: Cold / Warm (choose 1).</summary>
public class ModifierGroup : VersionedEntity
{
    public string Name { get; set; } = "";
    public int MinSelect { get; set; }
    public int MaxSelect { get; set; } = 1;
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
    public List<ModifierOption> Options { get; set; } = [];

    /// <summary>min ≤ max, max ≥ 1, and enough active options to meet the minimum.</summary>
    public static string? Problem(int min, int max, int activeOptions)
    {
        if (min < 0) return "The minimum can't be below 0.";
        if (max < 1) return "The maximum must be at least 1.";
        if (min > max) return "The minimum can't be more than the maximum.";
        if (min > activeOptions) return "There are fewer active options than the minimum to choose.";
        return null;
    }

    /// <summary>"choose 1", "choose up to 3", "choose 1 to 2".</summary>
    public static string ChoiceText(int min, int max) =>
        min == max ? $"choose {min}" : min == 0 ? $"choose up to {max}" : $"choose {min} to {max}";
}

public class ModifierOption : Entity
{
    public Guid GroupId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Added to the item price when chosen, including VAT.</summary>
    public decimal PriceAddOn { get; set; }

    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>Chosen already when the item's pop-up opens (e.g. Cold).</summary>
    public bool IsDefault { get; set; }
}

/// <summary>Gives a group to a category (all its items) or to one item: exactly one of the two.</summary>
public class ModifierGroupAssignment : Entity
{
    public Guid GroupId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? MenuItemId { get; set; }
}
