using Microsoft.EntityFrameworkCore;
using Tausi.Domain.Ledger;
using Tausi.Domain.Menu;
using Tausi.Domain.Money;
using Tausi.Domain.Stock;

namespace Tausi.Infrastructure.Seeding;

public sealed partial class Seeder
{
    /// <summary>Every business starts with a Store and a Bar; terminals without a location sell from the Bar.</summary>
    public async Task SeedStockLocations(CancellationToken ct)
    {
        if (!await db.StockLocations.AnyAsync(ct))
        {
            db.StockLocations.Add(new StockLocation { Name = StockLocation.Store, SortOrder = 10 });
            db.StockLocations.Add(new StockLocation { Name = StockLocation.Bar, SortOrder = 20 });
            await db.SaveChangesAsync(ct);
        }
        var bar = await db.StockLocations.Where(l => l.Name == StockLocation.Bar).Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
        if (bar is { } id)
            await db.Terminals.Where(t => t.SellsFromLocationId == null).ExecuteUpdateAsync(u => u.SetProperty(t => t.SellsFromLocationId, id), ct);
    }

    private sealed record DemoStock(
        string Name, StockCategory Category, StockUnit Unit, (string Name, decimal Factor)[] Packs, string CostPack, decimal PackCost,
        decimal Store, decimal Bar, decimal? BarReorder);

    /// <summary>Mau's stock, in base units, with the cost of one pack (or one base unit when the pack is "").</summary>
    private static readonly DemoStock[] DemoItems =
    [
        new("Tusker Lager", StockCategory.Beer, StockUnit.Bottle, [("crate", 25)], "crate", 2500, 250, 50, 24),
        new("Guiness", StockCategory.Beer, StockUnit.Bottle, [("crate", 25)], "crate", 3000, 150, 25, 24),
        new("White Cap Lager", StockCategory.Beer, StockUnit.Bottle, [("crate", 25)], "crate", 2800, 150, 18, 24),
        new("Balozi Lager", StockCategory.Beer, StockUnit.Bottle, [("crate", 25)], "crate", 2500, 100, 20, 12),
        new("Heineken", StockCategory.Beer, StockUnit.Bottle, [("crate", 24)], "crate", 3600, 48, 12, 12),
        new("Faxe Can", StockCategory.Beer, StockUnit.Can, [("tray", 24)], "tray", 2400, 48, 12, 12),
        new("Viceroy 375ml", StockCategory.Spirits, StockUnit.Ml, [("bottle", 375), ("carton", 9000)], "bottle", 600, 9000, 1125, 750),
        new("Gilbeys 350ml", StockCategory.Spirits, StockUnit.Ml, [("bottle", 350), ("carton", 8400)], "bottle", 550, 8400, 700, 1050),
        new("Sodas 300ml", StockCategory.SoftDrinks, StockUnit.Bottle, [("crate", 24)], "crate", 1200, 120, 24, 24),
        new("Mau's Water 1L", StockCategory.SoftDrinks, StockUnit.Bottle, [("pack", 12)], "pack", 480, 48, 12, 12),
        new("Pineapple Punch mix", StockCategory.SoftDrinks, StockUnit.Ml, [("litre", 1000), ("jerrycan", 5000)], "litre", 200, 20000, 5000, 3000),
        new("Delmonte", StockCategory.SoftDrinks, StockUnit.Piece, [("pack", 12)], "pack", 2400, 24, 6, 6),
    ];

    /// <summary>Menu item → (stock item, quantity, unit). Pool Table has none: it sells time, not stock.</summary>
    private static readonly (string Menu, string Stock, decimal Quantity, string Unit)[] DemoRecipes =
    [
        ("Tusker Lager", "Tusker Lager", 1, "bottle"), ("Guiness", "Guiness", 1, "bottle"), ("White Cap Lager", "White Cap Lager", 1, "bottle"),
        ("Balozi Lager", "Balozi Lager", 1, "bottle"), ("Heineken", "Heineken", 1, "bottle"), ("Faxe Can", "Faxe Can", 1, "can"),
        ("Viceroy 375ml", "Viceroy 375ml", 1, "bottle"), ("Viceroy Tot", "Viceroy 375ml", 25, "ml"),
        ("Gilbeys 350ml", "Gilbeys 350ml", 1, "bottle"), ("Gilbeys Tot", "Gilbeys 350ml", 25, "ml"),
        ("Sodas 300ml", "Sodas 300ml", 1, "bottle"), ("Mau's Water 1L", "Mau's Water 1L", 1, "bottle"),
        ("Pineapple Punch", "Pineapple Punch mix", 300, "ml"), ("Delmonte", "Delmonte", 1, "piece"),
    ];

    /// <summary>
    /// The demo's stock: items with packs, tots on the menu, recipes for every drink, the supplier KBL,
    /// and an approved opening count of the Store and the Bar (so on hand and values show at once).
    /// Only into a business with no stock items yet.
    /// </summary>
    public async Task<bool> SeedDemoStock(CancellationToken ct)
    {
        await SeedStockLocations(ct);
        if (await db.StockItems.AnyAsync(ct)) return false;

        await SeedDemoTots(ct);
        var locations = await db.StockLocations.ToDictionaryAsync(l => l.Name, ct);
        var store = locations[StockLocation.Store];
        var bar = locations[StockLocation.Bar];
        var items = new Dictionary<string, StockItem>();
        foreach (var d in DemoItems)
        {
            var item = new StockItem
            {
                Name = d.Name, Category = d.Category, BaseUnit = d.Unit,
                Packs = d.Packs.Select(p => new StockPack(p.Name, p.Factor)).ToList(),
            };
            item.AverageCost = d.PackCost / item.FactorOf(d.CostPack);
            items[d.Name] = item;
            db.StockItems.Add(item);
            db.StockLevels.Add(new StockLevel { StockItemId = item.Id, LocationId = store.Id, OnHand = d.Store });
            db.StockLevels.Add(new StockLevel { StockItemId = item.Id, LocationId = bar.Id, OnHand = d.Bar, ReorderLevel = d.BarReorder });
        }
        foreach (var other in locations.Values.Where(l => l.Id != store.Id && l.Id != bar.Id))
        foreach (var item in items.Values)
            db.StockLevels.Add(new StockLevel { StockItemId = item.Id, LocationId = other.Id });

        var menu = await db.MenuItems.ToDictionaryAsync(m => m.Name, m => m.Id, ct);
        var order = new Dictionary<Guid, int>();
        foreach (var (menuName, stockName, quantity, unit) in DemoRecipes)
        {
            if (!menu.TryGetValue(menuName, out var menuId)) continue;
            var item = items[stockName];
            order[menuId] = order.GetValueOrDefault(menuId) + 10;
            db.RecipeLines.Add(new RecipeLine
            {
                MenuItemId = menuId, StockItemId = item.Id, Quantity = quantity, Unit = unit,
                BaseQuantity = quantity * item.FactorOf(unit), SortOrder = order[menuId],
            });
        }

        if (!await db.Suppliers.AnyAsync(ct))
            db.Suppliers.Add(new Supplier { Name = "KBL", Phone = "0711 000 000", Notes = "Kenya Breweries: beers." });

        // The opening counts, approved by the Administrator after MAU counted.
        var mau = await db.Users.Where(u => u.Name == "MAU").Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        var admin = await db.Users.Where(u => u.Name == "Administrator").Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var accounts = await db.LedgerAccounts.ToDictionaryAsync(a => a.Code, a => a.Id, ct);
        foreach (var location in new[] { store, bar })
        {
            var count = new StockCount
            {
                LocationId = location.Id, Kind = StockCountKind.Full, IsOpening = true, Status = StockCountStatus.Approved, AsAt = now,
                CountedByUserId = mau ?? admin ?? Guid.Empty, SubmittedAt = now, ApprovedByUserId = admin, ApprovedAt = now,
            };
            var value = 0m;
            foreach (var d in DemoItems)
            {
                var item = items[d.Name];
                var quantity = location == store ? d.Store : d.Bar;
                count.Lines.Add(new StockCountLine
                {
                    CountId = count.Id, StockItemId = item.Id, Counted = quantity, Expected = 0, UnitCost = item.AverageCost,
                    Entered = new Dictionary<string, decimal> { [item.BaseUnitName] = quantity },
                });
                db.StockMovements.Add(new StockMovement
                {
                    StockItemId = item.Id, LocationId = location.Id, Quantity = quantity, UnitCost = item.AverageCost,
                    Kind = StockMovementKind.Opening, ReferenceType = "Count", ReferenceId = count.Id, UserId = admin, At = now,
                });
                value += quantity * item.AverageCost;
            }
            db.StockCounts.Add(count);
            var amount = MoneyMath.Round(value);
            db.JournalEntries.Add(new JournalEntry
            {
                Date = now, Memo = $"Opening stock · {location.Name}", SourceType = "StockCount", SourceId = count.Id.ToString(),
                CreatedAt = now, CreatedByUserId = admin,
                Lines =
                [
                    new JournalLine { AccountId = accounts[LedgerAccountCodes.Stock], Debit = amount, SourceType = "StockCount", SourceId = count.Id.ToString() },
                    new JournalLine { AccountId = accounts[LedgerAccountCodes.OpeningBalances], Credit = amount, SourceType = "StockCount", SourceId = count.Id.ToString() },
                ],
            });
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Tots of the two spirits, so the bar can sell by the measure (25 ml).</summary>
    private async Task SeedDemoTots(CancellationToken ct)
    {
        var spirits = await db.Categories.FirstOrDefaultAsync(c => c.Name == "Spirits", ct);
        var standard = await db.PriceLists.FirstOrDefaultAsync(l => l.IsDefault, ct);
        if (spirits is null || standard is null) return;
        var sort = await db.MenuItems.Where(m => m.CategoryId == spirits.Id).MaxAsync(m => (int?)m.SortOrder, ct) ?? 0;
        foreach (var (name, price) in new[] { ("Viceroy Tot", 150m), ("Gilbeys Tot", 140m) })
        {
            if (await db.MenuItems.AnyAsync(m => m.Name == name, ct)) continue;
            var item = new MenuItem { Name = name, ShortName = MenuItem.DefaultShortName(name), CategoryId = spirits.Id, SortOrder = sort += 10 };
            item.Prices.Add(new MenuItemPrice { MenuItemId = item.Id, PriceListId = standard.Id, Price = price });
            db.MenuItems.Add(item);
        }
        await db.SaveChangesAsync(ct);
    }
}
