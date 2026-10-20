using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Auditing;
using Nexagen.Application.Common;
using Nexagen.Domain.Common;
using Nexagen.Domain.Menu;

namespace Nexagen.Application.Menu;

/// <summary>
/// Adds or edits an item's details (menu.edit). Prices are never accepted here: they go through
/// <see cref="SetItemPrices"/>. A new item is not sold until it has a price on the default list.
/// </summary>
public sealed class SaveItem(INexagenDb db, MenuAccess access, IAuditLog audit)
{
    public async Task<Guid> Execute(Guid? id, ItemRequest request, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        var name = MenuAccess.ValidName(request.Name, MenuItem.MinNameLength, MenuItem.MaxNameLength, "item");
        var upper = name.ToUpperInvariant();
        if (await db.MenuItems.AnyAsync(i => i.Name.ToUpper() == upper && i.Id != id, ct))
            throw new DomainException($"There is already an item called {name}.", MenuCodes.NameTaken);
        var shortName = string.IsNullOrWhiteSpace(request.ShortName) ? MenuItem.DefaultShortName(name) : request.ShortName.Trim();
        if (shortName.Length > MenuItem.MaxShortNameLength)
            throw new DomainException($"The short name can be at most {MenuItem.MaxShortNameLength} characters.", MenuCodes.ShortNameTooLong);
        MenuAccess.CheckVat(request.VatRate);
        if (!await db.Categories.AnyAsync(c => c.Id == request.CategoryId, ct))
            throw new DomainException("Choose a category.");

        MenuItem item;
        object? before = null;
        if (id is { } existing)
        {
            item = await db.MenuItems.FirstOrDefaultAsync(i => i.Id == existing, ct)
                ?? throw new NotFoundException("Item not found.");
            MenuAccess.CheckVersion(item, request.RowVersion, (DbContext)db);
            before = View(item);
        }
        else
        {
            var last = await db.MenuItems.Where(i => i.CategoryId == request.CategoryId).MaxAsync(i => (int?)i.SortOrder, ct) ?? 0;
            item = new MenuItem { SortOrder = last + 10 };
            db.MenuItems.Add(item);
        }
        item.Name = name;
        item.ShortName = shortName;
        item.CategoryId = request.CategoryId;
        item.VatRate = request.VatRate;
        item.Active = request.Active;
        item.ErpItemCode = string.IsNullOrWhiteSpace(request.ErpItemCode) ? null : request.ErpItemCode.Trim();
        if (request.SortOrder is { } sort) item.SortOrder = sort;

        if (request.GroupIds is { } groupIds)
            await SetOwnGroups(item.Id, groupIds, ct);

        audit.Write(new AuditRecord(AuditActions.MenuItemChanged, nameof(MenuItem), item.Id.ToString(),
            before, View(item), grant.ApprovedByUserId));
        await MenuAccess.Save(db, ct);
        await access.Changed(ct);
        return item.Id;
    }

    private async Task SetOwnGroups(Guid itemId, IReadOnlyList<Guid> groupIds, CancellationToken ct)
    {
        var wanted = groupIds.Distinct().ToList();
        if (wanted.Count > 0 && await db.ModifierGroups.CountAsync(g => wanted.Contains(g.Id), ct) != wanted.Count)
            throw new DomainException("Unknown option group.", MenuCodes.GroupInvalid);
        var current = await db.ModifierGroupAssignments.Where(a => a.MenuItemId == itemId).ToListAsync(ct);
        db.ModifierGroupAssignments.RemoveRange(current.Where(a => !wanted.Contains(a.GroupId)));
        foreach (var g in wanted.Where(g => current.All(a => a.GroupId != g)))
            db.ModifierGroupAssignments.Add(new ModifierGroupAssignment { GroupId = g, MenuItemId = itemId });
    }

    private static object View(MenuItem i) =>
        new { i.Name, i.ShortName, i.CategoryId, i.VatRate, i.Active, i.ErpItemCode };
}

/// <summary>
/// Sets an item's prices (menu.edit_prices). One price-log row per changed price, with the approver
/// when approval was needed. Negative prices are refused; 0 needs <c>AllowZero</c>. The default
/// list always keeps a price.
/// </summary>
public sealed class SetItemPrices(INexagenDb db, MenuAccess access, IAuditLog audit, TimeProvider clock)
{
    public async Task<int> Execute(Guid itemId, ItemPricesRequest request, CancellationToken ct)
    {
        var grant = await access.RequirePrices(ct);
        var item = await db.MenuItems.Include(i => i.Prices).FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new NotFoundException("Item not found.");
        MenuAccess.CheckVersion(item, request.RowVersion, (DbContext)db);
        var lists = await db.PriceLists.ToDictionaryAsync(l => l.Id, ct);
        var defaultList = lists.Values.FirstOrDefault(l => l.IsDefault)
            ?? throw new ConflictException("There is no default price list.");

        foreach (var (listId, price) in request.Prices)
        {
            if (!lists.ContainsKey(listId)) throw new DomainException("Unknown price list.");
            CheckPrice(price, request.AllowZero);
            if (price is null && listId == defaultList.Id)
                throw new DomainException($"Every item needs a price on {defaultList.Name}.", MenuCodes.DefaultPriceRequired);
        }
        var defaultAfter = request.Prices.TryGetValue(defaultList.Id, out var d) ? d : MenuRules.PriceOn(item, defaultList.Id);
        if (defaultAfter is null)
            throw new DomainException($"Every item needs a price on {defaultList.Name}.", MenuCodes.DefaultPriceRequired);

        var now = clock.GetUtcNow();
        var changes = new List<object>();
        foreach (var (listId, price) in request.Prices)
        {
            var row = item.Prices.FirstOrDefault(p => p.PriceListId == listId);
            var old = row?.Price;
            if (old == price) continue;
            if (price is null) db.MenuItemPrices.Remove(row!);
            else if (row is null) db.MenuItemPrices.Add(new MenuItemPrice { MenuItemId = item.Id, PriceListId = listId, Price = price.Value });
            else row.Price = price.Value;

            db.PriceChangeLogs.Add(new PriceChangeLog
            {
                MenuItemId = item.Id, PriceListId = listId, OldPrice = old, NewPrice = price,
                ChangedByUserId = access.User.UserId, ApprovedByUserId = grant.ApprovedByUserId,
                TerminalId = access.TerminalId, Source = PriceChangeSources.Admin, At = now,
            });
            changes.Add(new { list = lists[listId].Name, old, @new = price });
        }
        if (changes.Count == 0) return 0;

        // A price change is a change to the item: its version moves on, so stale editors are told.
        ((DbContext)db).Entry(item).State = EntityState.Modified;
        audit.Write(new AuditRecord(AuditActions.MenuPricesChanged, nameof(MenuItem), item.Id.ToString(),
            null, new { item = item.Name, changes }, grant.ApprovedByUserId));
        await MenuAccess.Save(db, ct);
        await access.Changed(ct);
        return changes.Count;
    }

    public static void CheckPrice(decimal? price, bool allowZero)
    {
        if (price < 0m) throw new DomainException("A price can't be negative.", MenuCodes.PriceNegative);
        if (price == 0m && !allowZero)
            throw new DomainException("A price of 0 makes the item free. Confirm to save it.", MenuCodes.PriceZero);
        if (price is { } p && decimal.Round(p, 2) != p)
            throw new DomainException("Prices are in whole cents.", MenuCodes.PriceNegative);
    }
}
