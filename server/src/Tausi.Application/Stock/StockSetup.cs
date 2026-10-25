using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Menu;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Stock;

namespace Tausi.Application.Stock;

public sealed record SaveLocationRequest(string Name, bool Active, long? RowVersion);

public sealed record LocationRowDto(Guid Id, string Name, bool Active, int SortOrder, bool InUse, long RowVersion);

/// <summary>
/// Stock locations (Store and Bar to start): add, rename, switch off. Needs stock.adjust. A location
/// with stock movements can be switched off but keeps its name in the history.
/// </summary>
public sealed class StockLocations(ITausiDb db, ICurrentSession session, IPermissionService permissions, IAuditLog audit, IPosEvents events)
{
    public async Task<IReadOnlyList<LocationRowDto>> List(CancellationToken ct)
    {
        session.RequireUser();
        var used = await db.StockMovements.AsNoTracking().Select(m => m.LocationId).Distinct().ToListAsync(ct);
        return (await db.StockLocations.AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct))
            .Select(l => new LocationRowDto(l.Id, l.Name, l.Active, l.SortOrder, used.Contains(l.Id), l.RowVersion)).ToList();
    }

    public async Task<LocationRowDto> Save(Guid? id, SaveLocationRequest r, CancellationToken ct)
    {
        var user = session.RequireUser();
        await permissions.Require(user, PermissionKeys.StockAdjust, ct: ct);
        var name = MenuAccess.ValidName(r.Name, 2, StockLocation.MaxNameLength, "The location name");
        if (await db.StockLocations.AnyAsync(l => l.Name == name && l.Id != id, ct))
            throw new FieldErrorsException(new Dictionary<string, string> { ["name"] = "There is already a location with that name." });
        StockLocation location;
        object? old = null;
        if (id is { } existing)
        {
            location = await db.StockLocations.FirstOrDefaultAsync(l => l.Id == existing, ct) ?? throw new NotFoundException("Location not found.");
            if (r.RowVersion is { } version) MenuAccess.CheckVersion(location, version, (DbContext)db);
            old = new { location.Name, location.Active };
            if (!r.Active && location.Active && await db.Terminals.AnyAsync(t => t.SellsFromLocationId == location.Id && t.RevokedAt == null, ct))
                throw new DomainException("A terminal sells from this location. Choose another for it first (Terminals and printers).", "location_in_use");
        }
        else
        {
            location = new StockLocation { SortOrder = ((await db.StockLocations.MaxAsync(l => (int?)l.SortOrder, ct)) ?? 0) + 10 };
            db.StockLocations.Add(location);
        }
        location.Name = name;
        location.Active = r.Active;
        audit.Write(new AuditRecord(AuditActions.StockLocationChanged, nameof(StockLocation), location.Id.ToString(), old,
            new { location.Name, location.Active }));
        await db.SaveChangesAsync(ct);
        await StockEvents.Publish(events, "locations", ct);
        return (await List(ct)).Single(l => l.Id == location.Id);
    }
}

public sealed record StockSuggestion(
    Guid MenuItemId, string MenuItem, string Category, string StockItemName, string StockCategory, string BaseUnit, string? PackName,
    decimal? PackFactor, Guid? ExistingStockItemId, bool Suggested, string? Hint);

public sealed record CreateFromMenuRequest(IReadOnlyList<StockSuggestion> Items);

/// <summary>
/// The setup helper: for bottled menu items with no recipe, a 1:1 stock item and recipe ("Tusker
/// Lager sells 1 bottle of Tusker Lager") with a pack the owner can change (crate = 25). Spirits sold
/// in tots, and anything that isn't one-bottle-per-sale, are left for the recipe screen.
/// </summary>
public sealed class StockFromMenu(ITausiDb db, ICurrentSession session, MenuAccess menu, IAuditLog audit, IPosEvents events)
{
    public async Task<IReadOnlyList<StockSuggestion>> Suggestions(CancellationToken ct)
    {
        await menu.RequireEdit(ct);
        var withRecipe = await db.RecipeLines.AsNoTracking().Select(r => r.MenuItemId).Distinct().ToListAsync(ct);
        var stock = await db.StockItems.AsNoTracking().ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var items = await db.MenuItems.AsNoTracking().Where(i => i.Active && !withRecipe.Contains(i.Id)).OrderBy(i => i.Name).ToListAsync(ct);
        return items.Select(i =>
        {
            var category = categories.GetValueOrDefault(i.CategoryId, "");
            var kind = Kind(i.Name, category);
            var existing = stock.FirstOrDefault(s => string.Equals(s.Name, i.Name, StringComparison.OrdinalIgnoreCase));
            var (pack, factor) = Pack(kind, i.Name);
            var tot = IsTot(i.Name, category);
            var hint = tot ? "Sold in tots: set its recipe on the recipe screen (25 ml of the bottle)." : kind is null ? "Not a bottle or can: set its recipe later if it uses stock." : null;
            return new StockSuggestion(i.Id, i.Name, category, i.Name, (kind ?? StockCategory.Other).ToString(),
                (IsCan(i.Name) ? StockUnit.Can : StockUnit.Bottle).ToString(), pack, factor, existing?.Id, !tot && kind is not null, hint);
        }).ToList();
    }

    public async Task<int> Create(CreateFromMenuRequest r, CancellationToken ct)
    {
        await menu.RequireEdit(ct);
        session.RequireUser();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var stock = await db.StockItems.ToListAsync(ct);
        var created = 0;
        foreach (var s in r.Items ?? [])
        {
            if (!await db.MenuItems.AnyAsync(i => i.Id == s.MenuItemId, ct)) continue;
            if (await db.RecipeLines.AnyAsync(x => x.MenuItemId == s.MenuItemId, ct)) continue;
            var name = MenuAccess.ValidName(s.StockItemName, 2, StockItem.MaxNameLength, "The stock item name");
            if (!Enum.TryParse<StockCategory>(s.StockCategory, out var category)) category = StockCategory.Other;
            if (!Enum.TryParse<StockUnit>(s.BaseUnit, out var unit)) unit = StockUnit.Bottle;
            var item = stock.FirstOrDefault(x => x.Id == s.ExistingStockItemId)
                       ?? stock.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new StockItem { Name = name, Category = category, BaseUnit = unit };
                if (s.PackName is { Length: > 0 } pack && s.PackFactor is > 1)
                    item.Packs.Add(new StockPack(pack.Trim().ToLowerInvariant(), s.PackFactor.Value));
                db.StockItems.Add(item);
                stock.Add(item);
                created++;
            }
            db.RecipeLines.Add(new RecipeLine
            {
                MenuItemId = s.MenuItemId, StockItemId = item.Id, Quantity = 1, Unit = item.BaseUnitName, BaseQuantity = 1, SortOrder = 10,
            });
        }
        audit.Write(new AuditRecord(AuditActions.StockItemChanged, nameof(StockItem), null, null,
            new { summary = $"From the menu: {created} stock items, {r.Items?.Count ?? 0} recipes" }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await StockEvents.Publish(events, "items", ct);
        return created;
    }

    private static bool IsTot(string name, string category) =>
        $"{name} {category}".Contains("tot", StringComparison.OrdinalIgnoreCase)
        || name.Contains("shot", StringComparison.OrdinalIgnoreCase) || name.Contains("double", StringComparison.OrdinalIgnoreCase);

    private static bool IsCan(string name) => name.Contains("can", StringComparison.OrdinalIgnoreCase);

    /// <summary>What kind of bottle it looks like, from its name and category; null when it doesn't look like one.</summary>
    private static StockCategory? Kind(string name, string category)
    {
        var text = $"{name} {category}".ToLowerInvariant();
        if (new[] { "beer", "lager", "cider", "stout", "guinness", "guiness", "tusker", "pilsner", "smooth" }.Any(text.Contains)) return StockCategory.Beer;
        if (new[] { "soda", "soft", "water", "juice", "energy", "coke", "fanta", "sprite" }.Any(text.Contains)) return StockCategory.SoftDrinks;
        if (new[] { "spirit", "whisky", "whiskey", "vodka", "gin", "rum", "brandy", "liqueur" }.Any(text.Contains) || System.Text.RegularExpressions.Regex.IsMatch(text, @"\d+\s?ml"))
            return StockCategory.Spirits;
        return null;
    }

    private static (string? Pack, decimal? Factor) Pack(StockCategory? kind, string name) => kind switch
    {
        StockCategory.Beer when IsCan(name) => ("tray", 24m),
        StockCategory.Beer => ("crate", 25m),
        StockCategory.SoftDrinks => ("crate", 24m),
        StockCategory.Spirits => ("carton", 12m),
        _ => (null, null),
    };
}
