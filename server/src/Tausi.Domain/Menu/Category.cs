using Tausi.Domain.Common;

namespace Tausi.Domain.Menu;

/// <summary>A menu category (Beers, Spirits...). Never deleted, only deactivated.</summary>
public class Category : VersionedEntity
{
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
}
