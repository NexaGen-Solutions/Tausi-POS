using Tausi.Domain.Common;

namespace Tausi.Domain.Service;

/// <summary>A part of the venue on the table map, e.g. Main, Pool or Bar.</summary>
public class Area : VersionedEntity
{
    public const int MaxNameLength = 40;

    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
}
