using Tausi.Domain.Common;

namespace Tausi.Domain.Menu;

/// <summary>A menu category (Beers, Spirits...). Never deleted, only deactivated.</summary>
public class Category : VersionedEntity
{
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>
    /// How many items the order screen shows at a time, with page buttons; null shows them all on
    /// one page that scrolls.
    /// </summary>
    public int? ItemsPerPage { get; set; }

    public const int MinItemsPerPage = 4;
    public const int MaxItemsPerPage = 60;
}
