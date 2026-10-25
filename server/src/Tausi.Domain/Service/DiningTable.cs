using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

/// <summary>A table on the map. The name is text, so "7", "Pool 1" and "Bar 2" all work.</summary>
public class DiningTable : VersionedEntity
{
    /// <summary>Where it was imported from, e.g. "sambapos:menuitem:12:portion:3", so a re-import matches it.</summary>
    public string? ExternalRef { get; set; }

    public const int MaxNameLength = 40;

    public string Name { get; set; } = "";
    public Guid AreaId { get; set; }
    public Area? Area { get; set; }
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
}
