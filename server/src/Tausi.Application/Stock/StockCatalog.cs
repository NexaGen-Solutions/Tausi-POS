using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Stock;

namespace Tausi.Application.Stock;

public sealed record StockLocationDto(Guid Id, string Name, bool Active);

public sealed record StockPackDto(string Name, decimal Factor);

public sealed record StockLevelDto(Guid LocationId, decimal OnHand, string OnHandText, decimal? ReorderLevel);

/// <summary>
/// One row of the stock page. <c>OnHand</c> is at the chosen location (or all of them); orange rows
/// are <c>BelowReorder</c>, and <c>Negative</c> ones say "Below zero: check deliveries or the last count".
/// </summary>
public sealed record StockItemRow(
    Guid Id, string Name, string Category, string BaseUnit, IReadOnlyList<StockPackDto> Packs, bool Active,
    decimal OnHand, string OnHandText, decimal? ReorderLevel, decimal AverageCost, decimal Value,
    bool BelowReorder, bool Negative, bool HasMovements, IReadOnlyList<StockLevelDto> Levels, long RowVersion);

public sealed record NoRecipeItem(Guid MenuItemId, string Name);

public sealed record StockOverviewDto(
    IReadOnlyList<StockLocationDto> Locations, IReadOnlyList<StockItemRow> Items, IReadOnlyList<NoRecipeItem> NoRecipe,
    decimal TotalValue, bool CanEdit);

public sealed record StockMovementDto(
    DateTimeOffset At, string Kind, decimal Quantity, string QuantityText, string Location, string? User, string Reference, decimal UnitCost);

public sealed record SaveStockItemRequest(
    string Name, StockCategory Category, StockUnit BaseUnit, List<StockPackDto>? Packs, bool Active,
    Dictionary<Guid, decimal?>? ReorderLevels, long? RowVersion);

public sealed record RecipeLineDto(Guid StockItemId, string StockItem, decimal Quantity, string Unit, IReadOnlyList<string> Units, decimal Cost);

/// <summary>A menu item's recipe with what one sale costs, its Standard price and the margin on the price without VAT.</summary>
public sealed record RecipeDto(
    Guid MenuItemId, string MenuItem, IReadOnlyList<RecipeLineDto> Lines, decimal CostPerSale, decimal? Price, decimal? MarginPercent,
    decimal? PriceWithoutVat = null);

public sealed record SaveRecipeLine(Guid StockItemId, decimal Quantity, string? Unit);

public sealed record SaveRecipeRequest(List<SaveRecipeLine>? Lines);

public sealed record RecipeMenuItem(Guid Id, string Name, string Category, bool HasRecipe);

public sealed class StockCatalog(ITausiDb db, ICurrentSession session, StockAccess access, IPermissionService permissions)
{
    public async Task<StockOverviewDto> Overview(Guid? locationId, CancellationToken ct)
    {
        var user = session.RequireUser();
        await access.RequireAny(user, ct);
        var locations = await db.StockLocations.AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct);
        var items = await db.StockItems.AsNoTracking().OrderBy(i => i.Category).ThenBy(i => i.Name).ToListAsync(ct);
        var levels = await db.StockLevels.AsNoTracking().ToListAsync(ct);
        var moved = (await db.StockMovements.AsNoTracking().Select(m => m.StockItemId).Distinct().ToListAsync(ct)).ToHashSet();

        var rows = items.Select(i =>
        {
            var mine = levels.Where(l => l.StockItemId == i.Id && (locationId == null || l.LocationId == locationId)).ToList();
            var onHand = mine.Sum(l => l.OnHand);
            var reorder = mine.Any(l => l.ReorderLevel != null) ? mine.Sum(l => l.ReorderLevel ?? 0) : (decimal?)null;
            return new StockItemRow(
                i.Id, i.Name, i.Category.ToString(), i.BaseUnitName, Packs(i), i.Active, onHand, i.Describe(onHand), reorder,
                i.AverageCost, MoneyMath.Round(onHand * i.AverageCost), reorder is { } r && onHand < r,
                mine.Any(l => l.OnHand < 0), moved.Contains(i.Id),
                levels.Where(l => l.StockItemId == i.Id)
                    .Select(l => new StockLevelDto(l.LocationId, l.OnHand, i.Describe(l.OnHand), l.ReorderLevel)).ToList(),
                i.RowVersion);
        }).ToList();

        return new StockOverviewDto(
            locations.Select(l => new StockLocationDto(l.Id, l.Name, l.Active)).ToList(), rows, await NoRecipe(ct),
            rows.Where(r => r.Active).Sum(r => r.Value),
            await permissions.Check(user, PermissionKeys.MenuEdit, ct) == PermissionValue.Allowed);
    }

    /// <summary>Active menu items without a recipe: they don't reduce stock.</summary>
    public async Task<IReadOnlyList<NoRecipeItem>> NoRecipe(CancellationToken ct)
    {
        var withRecipe = db.RecipeLines.Select(r => r.MenuItemId);
        return await db.MenuItems.AsNoTracking().Where(m => m.Active && !withRecipe.Contains(m.Id))
            .OrderBy(m => m.Name).Select(m => new NoRecipeItem(m.Id, m.Name)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StockMovementDto>> History(Guid itemId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await access.RequireAny(session.RequireUser(), ct);
        var item = await db.StockItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new NotFoundException("Stock item not found.");
        var zone = BusinessTimeZoneFor(await SettingsRegistry.Load(db, ct));
        var query = db.StockMovements.AsNoTracking().Where(m => m.StockItemId == itemId);
        if (from is { } f) query = query.Where(m => m.At >= Start(f, zone));
        if (to is { } t) query = query.Where(m => m.At < Start(t.AddDays(1), zone));
        var movements = await query.OrderByDescending(m => m.At).Take(500).ToListAsync(ct);
        var locations = await db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var userIds = movements.Where(m => m.UserId != null).Select(m => m.UserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var refs = await References(movements, ct);
        return movements.Select(m => new StockMovementDto(
            m.At, m.Kind.ToString(), m.Quantity, item.Describe(m.Quantity), locations.GetValueOrDefault(m.LocationId, "?"),
            m.UserId is { } u ? users.GetValueOrDefault(u) : null, refs.GetValueOrDefault(m.Id, m.ReferenceType), m.UnitCost)).ToList();
    }

    /// <summary>"#1042 · Tusker Lager", "Delivery KBL 4471", "Count · Bar"…</summary>
    private async Task<Dictionary<Guid, string>> References(List<StockMovement> movements, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        var lineIds = movements.Where(m => m.ReferenceType == StockSelling.SaleReference && m.ReferenceId != null).Select(m => m.ReferenceId!.Value).ToList();
        var lines = await db.OrderLines.AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .Join(db.Tickets, l => l.TicketId, k => k.Id, (l, k) => new { l.Id, k.Number, l.Name }).ToDictionaryAsync(x => x.Id, ct);
        var deliveryIds = movements.Where(m => m.ReferenceType == StockReferences.Delivery).Select(m => m.ReferenceId).ToList();
        var deliveries = await db.Deliveries.AsNoTracking().Where(d => deliveryIds.Contains(d.Id))
            .Join(db.Suppliers, d => d.SupplierId, s => s.Id, (d, s) => new { d.Id, s.Name, d.InvoiceNumber }).ToDictionaryAsync(x => x.Id, ct);
        foreach (var m in movements)
        {
            var id = m.ReferenceId ?? Guid.Empty;
            result[m.Id] = m.ReferenceType switch
            {
                StockSelling.SaleReference when lines.TryGetValue(id, out var l) => $"#{l.Number} · {l.Name}",
                StockReferences.Delivery when deliveries.TryGetValue(id, out var d) => $"Delivery · {d.Name} {d.InvoiceNumber}",
                StockReferences.Waste => "Waste",
                StockReferences.Transfer => "Transfer",
                StockReferences.Count => "Count",
                _ => m.ReferenceType,
            };
        }
        return result;
    }

    public async Task<IReadOnlyList<StockLocationDto>> Locations(CancellationToken ct)
    {
        session.RequireUser();
        return await db.StockLocations.AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
            .Select(l => new StockLocationDto(l.Id, l.Name, l.Active)).ToListAsync(ct);
    }

    public static IReadOnlyList<StockPackDto> Packs(StockItem item) =>
        item.Packs.OrderBy(p => p.Factor).Select(p => new StockPackDto(p.Name, p.Factor)).ToList();

    private static TimeZoneInfo BusinessTimeZoneFor(SettingValues v) =>
        Domain.Settings.BusinessTimeZone.Find(v.String(SettingsRegistry.BusinessTimeZoneKey));

    public static DateTimeOffset Start(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

public static class StockReferences
{
    public const string Delivery = "Delivery";
    public const string Waste = "Waste";
    public const string Transfer = "Transfer";
    public const string Count = "Count";
}

/// <summary>Adding and changing stock items and recipes (menu.edit), audited.</summary>
public sealed class StockItemCommands(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval, StockBook book,
    IAuditLog audit, IPosEvents events)
{
    public async Task<Guid> Save(Guid? id, SaveStockItemRequest r, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.MenuEdit, approval.ApprovalToken, ct);
        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > StockItem.MaxNameLength) throw new DomainException($"Give it a name of 2 to {StockItem.MaxNameLength} characters.", "name_invalid");
        if (await db.StockItems.AnyAsync(i => i.Id != id && i.Name.ToLower() == name.ToLower(), ct))
            throw new DomainException($"There is already a stock item called {name}.", "name_taken");
        var packs = CheckPacks(r.Packs, r.BaseUnit);

        StockItem item;
        object? before = null;
        if (id is { } existing)
        {
            item = await db.StockItems.FirstOrDefaultAsync(i => i.Id == existing, ct) ?? throw new NotFoundException("Stock item not found.");
            if (r.RowVersion is { } version && version != item.RowVersion)
                throw new ConflictException("Someone else changed this item. Reload and try again.");
            if (item.BaseUnit != r.BaseUnit && await db.StockMovements.AnyAsync(m => m.StockItemId == existing, ct))
                throw new DomainException(
                    $"{item.Name} already has stock movements in {item.BaseUnitName}s, so its base unit can't change. Add a new item instead.",
                    StockCodes.BaseUnitLocked);
            before = new { item.Name, category = item.Category.ToString(), baseUnit = item.BaseUnit.ToString(), item.Packs, item.Active };
        }
        else
        {
            item = new StockItem();
            db.StockItems.Add(item);
        }
        item.Name = name;
        item.Category = r.Category;
        item.BaseUnit = r.BaseUnit;
        item.Packs = packs;
        item.Active = r.Active;

        await book.EnsureLevels(ct);
        await db.SaveChangesAsync(ct);
        foreach (var (locationId, level) in r.ReorderLevels ?? [])
        {
            if (level is < 0) throw new DomainException("A reorder level can't be below 0.");
            await db.StockLevels.Where(l => l.StockItemId == item.Id && l.LocationId == locationId)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.ReorderLevel, level), ct);
        }

        audit.Write(new AuditRecord(AuditActions.StockItemChanged, nameof(StockItem), item.Id.ToString(), before,
            new { item.Name, category = item.Category.ToString(), baseUnit = item.BaseUnit.ToString(), item.Packs, item.Active, reorder = r.ReorderLevels },
            grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await StockEvents.Publish(events, "items", ct);
        return item.Id;
    }

    private static List<StockPack> CheckPacks(List<StockPackDto>? packs, StockUnit baseUnit)
    {
        var result = new List<StockPack>();
        foreach (var p in packs ?? [])
        {
            var name = (p.Name ?? "").Trim().ToLowerInvariant();
            if (name.Length is 0 or > StockItem.MaxPackNameLength) throw new DomainException("Each pack needs a name, e.g. crate.", "pack_invalid");
            if (p.Factor <= 1) throw new DomainException($"A {name} must hold more than one {StockItem.UnitName(baseUnit)}.", "pack_invalid");
            if (name == StockItem.UnitName(baseUnit) && baseUnit is not (StockUnit.Ml or StockUnit.G))
                throw new DomainException($"\"{name}\" is the base unit already.", "pack_invalid");
            if (result.Any(x => x.Name == name)) throw new DomainException($"There are two packs called {name}.", "pack_invalid");
            result.Add(new StockPack(name, p.Factor));
        }
        return result.OrderBy(x => x.Factor).ToList();
    }
}

public sealed class RecipeCommands(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval, StockAccess access,
    IAuditLog audit, IPosEvents events)
{
    public async Task<IReadOnlyList<RecipeMenuItem>> MenuItems(CancellationToken ct)
    {
        await access.RequireAny(session.RequireUser(), ct);
        var withRecipe = (await db.RecipeLines.Select(r => r.MenuItemId).Distinct().ToListAsync(ct)).ToHashSet();
        return (await db.MenuItems.AsNoTracking().Include(m => m.Category).Where(m => m.Active).OrderBy(m => m.Name).ToListAsync(ct))
            .Select(m => new RecipeMenuItem(m.Id, m.Name, m.Category?.Name ?? "", withRecipe.Contains(m.Id))).ToList();
    }

    public async Task<RecipeDto> Get(Guid menuItemId, CancellationToken ct)
    {
        await access.RequireAny(session.RequireUser(), ct);
        return await View(menuItemId, ct);
    }

    /// <summary>Replaces the recipe (an empty list removes it). Audited with the old and new ingredients.</summary>
    public async Task<RecipeDto> Save(Guid menuItemId, SaveRecipeRequest r, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.MenuEdit, approval.ApprovalToken, ct);
        var menu = await db.MenuItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == menuItemId, ct) ?? throw new NotFoundException("Menu item not found.");
        var lines = r.Lines ?? [];
        var ids = lines.Select(l => l.StockItemId).Distinct().ToList();
        var items = await db.StockItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        if (ids.Count != lines.Count) throw new DomainException("Each stock item can be in the recipe once.");

        var before = await db.RecipeLines.Where(l => l.MenuItemId == menuItemId).ToListAsync(ct);
        var beforeView = before.Select(l => new { l.StockItemId, l.Quantity, l.Unit }).ToList();
        db.RecipeLines.RemoveRange(before);
        var order = 0;
        foreach (var line in lines)
        {
            if (!items.TryGetValue(line.StockItemId, out var item)) throw new DomainException("That stock item no longer exists.");
            if (line.Quantity <= 0) throw new DomainException($"Enter how much {item.Name} each sale uses.");
            var unit = string.IsNullOrWhiteSpace(line.Unit) ? item.BaseUnitName : line.Unit.Trim();
            var factor = item.FactorOf(unit);
            db.RecipeLines.Add(new RecipeLine
            {
                MenuItemId = menuItemId, StockItemId = item.Id, Quantity = line.Quantity, Unit = unit,
                BaseQuantity = line.Quantity * factor, SortOrder = order += 10,
            });
        }
        audit.Write(new AuditRecord(AuditActions.RecipeChanged, nameof(MenuItem), menuItemId.ToString(),
            new { menu = menu.Name, lines = beforeView },
            new { menu = menu.Name, lines = lines.Select(l => new { l.StockItemId, l.Quantity, l.Unit }) }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await StockEvents.Publish(events, "recipes", ct);
        return await View(menuItemId, ct);
    }

    private async Task<RecipeDto> View(Guid menuItemId, CancellationToken ct)
    {
        var menu = await db.MenuItems.AsNoTracking().Include(m => m.Prices).ThenInclude(p => p.PriceList)
            .FirstOrDefaultAsync(m => m.Id == menuItemId, ct) ?? throw new NotFoundException("Menu item not found.");
        var lines = await db.RecipeLines.AsNoTracking().Where(l => l.MenuItemId == menuItemId).OrderBy(l => l.SortOrder).ToListAsync(ct);
        var ids = lines.Select(l => l.StockItemId).ToList();
        var items = await db.StockItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var dto = lines.Select(l => new RecipeLineDto(l.StockItemId, items[l.StockItemId].Name, l.Quantity, l.Unit,
            items[l.StockItemId].UnitNames.ToList(), l.BaseQuantity * items[l.StockItemId].AverageCost)).ToList();
        var cost = MoneyMath.Round(dto.Sum(l => l.Cost));
        var price = menu.Prices.FirstOrDefault(p => p.PriceList?.IsDefault == true)?.Price;
        var vatDefault = (await SettingsRegistry.Load(db, ct)).Decimal(SettingsRegistry.BusinessVatRate);
        decimal? margin = null, net = null;
        if (price is > 0)
        {
            net = price.Value - VatCalculator.VatIncluded(price.Value, menu.EffectiveVatRate(vatDefault));
            margin = net > 0 ? decimal.Round((net.Value - cost) / net.Value * 100m, 1) : null;
        }
        return new RecipeDto(menu.Id, menu.Name, dto.Select(l => l with { Cost = MoneyMath.Round(l.Cost) }).ToList(), cost, price, margin, net);
    }
}
