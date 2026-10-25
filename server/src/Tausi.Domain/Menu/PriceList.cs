using Tausi.Domain.Common;

namespace Tausi.Domain.Menu;

/// <summary>
/// A set of prices (Standard, Price 2...). Exactly one is the default; a terminal can use another,
/// falling back to the default for items without a price on it.
/// </summary>
public class PriceList : VersionedEntity
{
    /// <summary>Where it was imported from, e.g. "sambapos:menuitem:12:portion:3", so a re-import matches it.</summary>
    public string? ExternalRef { get; set; }

    public const string StandardName = "Standard";
    public const string Price2Name = "Price 2";

    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public bool IsDefault { get; set; }
}
