using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Events;
using Tausi.Application.Menu;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Setup;

/// <summary>How to reach the SambaPOS database (read only).</summary>
public sealed record SambaPosConnection(string Server, string? Database, bool WindowsLogin, string? User, string? Password);

public sealed record SambaMenuItem(int Id, string Name, string? GroupCode);
public sealed record SambaPortion(int Id, int MenuItemId, string Name);
public sealed record SambaPrice(int PortionId, string? PriceTag, decimal Price);
public sealed record SambaTagGroup(int Id, string Name, int Min, int Max);
public sealed record SambaTag(int Id, int GroupId, string Name, decimal Price);
/// <summary>Which items an order tag group is offered on: an item, a group code, or everything (both empty).</summary>
public sealed record SambaTagMap(int GroupId, int? MenuItemId, string? GroupCode);
public sealed record SambaTable(int Id, string Name);
public sealed record SambaUser(int Id, string Name, string? Role);
public sealed record SambaScreenItem(int MenuItemId, string Category);

/// <summary>What could be read from SambaPOS, and what couldn't ("OrderTagMaps: table not found").</summary>
public sealed record SambaPosData(
    IReadOnlyList<SambaMenuItem> Items, IReadOnlyList<SambaPortion> Portions, IReadOnlyList<SambaPrice> Prices,
    IReadOnlyList<SambaTagGroup> TagGroups, IReadOnlyList<SambaTag> Tags, IReadOnlyList<SambaTagMap> TagMaps,
    IReadOnlyList<SambaTable> Tables, IReadOnlyList<SambaUser> Users, IReadOnlyList<SambaScreenItem> ScreenItems,
    IReadOnlyList<string> Problems);

/// <summary>Reads a SambaPOS database. Only SELECTs, on a read-only connection; never PINs or passwords.</summary>
public interface ISambaPosSource
{
    Task<string> Test(SambaPosConnection connection, CancellationToken ct);
    Task<SambaPosData> Read(SambaPosConnection connection, CancellationToken ct);
}

public sealed record SambaImportOptions(
    bool Menu = true, bool Prices = true, bool Modifiers = true, bool Tables = true, bool Users = true,
    bool CategoriesFromScreenMenu = false, IReadOnlyDictionary<string, string>? UserRoles = null, bool FirstPriceOnly = false);

public sealed record SambaImportRequest(SambaPosConnection Connection, SambaImportOptions? Options);

public sealed record SambaPreviewItem(string Name, string Category, string Status, IReadOnlyList<string> Prices);

public sealed record SambaPreviewUser(string Id, string Name, string? SambaRole, string? Role, string Status);

public sealed record SambaPreview(
    IReadOnlyList<SambaPreviewItem> Items, IReadOnlyList<string> Categories, IReadOnlyList<string> PriceLists,
    IReadOnlyList<string> ModifierGroups, IReadOnlyList<string> Tables, IReadOnlyList<SambaPreviewUser> Users,
    int NewItems, int ExistingItems, int NewTables, int NewUsers, IReadOnlyList<string> Problems);

public sealed record SambaImportResult(string Summary, IReadOnlyList<NewStaffPin> People, string? PinSheetPdf);

/// <summary>
/// The SambaPOS import (setup step 4, and later from the menu page): menu items by group code (or
/// screen-menu category), one item per portion ("Tusker (Large)"), price tags as price lists, order
/// tag groups as modifier groups, table entities as tables in "Main", and staff by name and role
/// (new PINs, never SambaPOS's). The SambaPOS ids are kept as ExternalRef, so importing again
/// matches what came before and never duplicates. Everything is written in one transaction.
/// With <see cref="SambaImportOptions.FirstPriceOnly"/> each SambaPOS item becomes one item with its
/// plain name and one price: its first portion's main price. Importing again that way renames items
/// an earlier import called "Item (Portion)" and hides the items it made from the other portions.
/// </summary>
public sealed class SambaPosImport(
    ITausiDb db, ISambaPosSource source, MenuAccess access, SetupPeople people, IAuditLog audit, IPosEvents events, TimeProvider clock)
{
    public async Task<string> Test(SambaPosConnection connection, CancellationToken ct)
    {
        await access.RequireEdit(ct);
        return await source.Test(connection, ct);
    }

    public async Task<SambaPreview> Preview(SambaImportRequest r, CancellationToken ct)
    {
        await access.RequireEdit(ct);
        var data = await source.Read(r.Connection, ct);
        return (await Plan(data, r.Options ?? new SambaImportOptions(), ct)).Preview;
    }

    public async Task<SambaImportResult> Apply(SambaImportRequest r, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        var options = r.Options ?? new SambaImportOptions();
        if (options.Prices) await access.RequirePrices(ct);
        var data = await source.Read(r.Connection, ct);
        var plan = await Plan(data, options, ct);
        var now = clock.GetUtcNow();
        var user = access.User;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lists = await db.PriceLists.ToListAsync(ct);
        var categories = await db.Categories.ToListAsync(ct);
        var groups = await db.ModifierGroups.Include(g => g.Options).ToListAsync(ct);
        var items = await db.MenuItems.Include(i => i.Prices).ToListAsync(ct);
        var defaultList = lists.First(l => l.IsDefault);
        int created = 0, priceLists = 0, modifierGroups = 0, tables = 0, renamed = 0, hidden = 0;

        PriceList ListFor(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return defaultList;
            var reference = $"sambapos:pricetag:{tag}";
            var list = lists.FirstOrDefault(l => l.ExternalRef == reference)
                       ?? lists.FirstOrDefault(l => string.Equals(l.Name, tag, StringComparison.OrdinalIgnoreCase));
            if (list is null)
            {
                list = new PriceList { Name = tag.Trim(), ExternalRef = reference };
                db.PriceLists.Add(list);
                lists.Add(list);
                priceLists++;
            }
            list.ExternalRef ??= reference;
            return list;
        }

        Category CategoryFor(string name)
        {
            var category = categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (category is not null) return category;
            category = new Category { Name = name, SortOrder = (categories.Count + 1) * 10 };
            db.Categories.Add(category);
            categories.Add(category);
            return category;
        }

        var itemsByMenuItem = new Dictionary<int, List<MenuItem>>();
        if (options.Menu)
        {
            foreach (var p in plan.Portions)
            {
                var item = items.FirstOrDefault(i => i.ExternalRef == p.Reference)
                           ?? items.FirstOrDefault(i => string.Equals(i.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                var category = CategoryFor(p.Category);
                if (item is null)
                {
                    item = new MenuItem
                    {
                        Name = p.Name, ShortName = MenuItem.DefaultShortName(p.Name), CategoryId = category.Id, ExternalRef = p.Reference,
                        SortOrder = (items.Count(i => i.CategoryId == category.Id) + 1) * 10,
                    };
                    db.MenuItems.Add(item);
                    items.Add(item);
                    created++;
                }
                item.ExternalRef ??= p.Reference;
                if (p.LongName is { } longName && item.ExternalRef == p.Reference
                    && string.Equals(item.Name, longName, StringComparison.OrdinalIgnoreCase)
                    && !items.Any(i => i != item && string.Equals(i.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (item.ShortName == MenuItem.DefaultShortName(item.Name)) item.ShortName = MenuItem.DefaultShortName(p.Name);
                    item.Name = p.Name;
                    renamed++;
                }
                if (!itemsByMenuItem.TryGetValue(p.MenuItemId, out var list)) itemsByMenuItem[p.MenuItemId] = list = [];
                list.Add(item);

                if (!options.Prices) continue;
                foreach (var price in p.Prices)
                {
                    var target = ListFor(price.Tag);
                    var existing = item.Prices.FirstOrDefault(x => x.PriceListId == target.Id);
                    if (existing?.Price == price.Price) continue;
                    if (existing is null)
                    {
                        existing = new MenuItemPrice { MenuItemId = item.Id, PriceListId = target.Id, Price = price.Price };
                        db.MenuItemPrices.Add(existing);
                        item.Prices.Add(existing);
                    }
                    else existing.Price = price.Price;
                    db.PriceChangeLogs.Add(new PriceChangeLog
                    {
                        MenuItemId = item.Id, PriceListId = target.Id, NewPrice = price.Price, ChangedByUserId = user.UserId,
                        Source = PriceChangeSources.Import, At = now,
                    });
                }
            }
        }

        if (options.Menu)
            foreach (var item in items.Where(i => i.Active && i.ExternalRef is { } x && plan.Skipped.Contains(x)))
            {
                item.Active = false;
                hidden++;
            }

        if (options.Modifiers)
        {
            foreach (var g in data.TagGroups)
            {
                var reference = $"sambapos:ordertaggroup:{g.Id}";
                var group = groups.FirstOrDefault(x => x.ExternalRef == reference)
                            ?? groups.FirstOrDefault(x => string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase));
                if (group is null)
                {
                    group = new ModifierGroup { Name = g.Name, MinSelect = g.Min, MaxSelect = Math.Max(1, g.Max), SortOrder = (groups.Count + 1) * 10, ExternalRef = reference };
                    db.ModifierGroups.Add(group);
                    groups.Add(group);
                    modifierGroups++;
                }
                group.ExternalRef ??= reference;
                var sort = group.Options.Count * 10;
                foreach (var tag in data.Tags.Where(t => t.GroupId == g.Id))
                {
                    if (group.Options.Any(o => string.Equals(o.Name, tag.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    var option = new ModifierOption { GroupId = group.Id, Name = tag.Name, PriceAddOn = tag.Price, SortOrder = sort += 10 };
                    group.Options.Add(option);
                    db.ModifierOptions.Add(option);
                }
                // Where it is offered: on items, on categories (group codes), or everywhere.
                var assignments = await db.ModifierGroupAssignments.Where(a => a.GroupId == group.Id).ToListAsync(ct);
                foreach (var map in data.TagMaps.Where(m => m.GroupId == g.Id))
                {
                    if (map.MenuItemId is { } menuItem && itemsByMenuItem.TryGetValue(menuItem, out var targets))
                        foreach (var target in targets.Where(t => assignments.All(a => a.MenuItemId != t.Id)))
                        {
                            var a = new ModifierGroupAssignment { GroupId = group.Id, MenuItemId = target.Id };
                            db.ModifierGroupAssignments.Add(a);
                            assignments.Add(a);
                        }
                    else if (map.MenuItemId is null)
                        foreach (var category in map.GroupCode is { Length: > 0 } code
                                     ? categories.Where(c => string.Equals(c.Name, code, StringComparison.OrdinalIgnoreCase))
                                     : categories.Where(c => plan.Categories.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
                        {
                            if (assignments.Any(a => a.CategoryId == category.Id)) continue;
                            var a = new ModifierGroupAssignment { GroupId = group.Id, CategoryId = category.Id };
                            db.ModifierGroupAssignments.Add(a);
                            assignments.Add(a);
                        }
                }
            }
        }

        if (options.Tables && data.Tables.Count > 0)
        {
            var area = await db.Areas.FirstOrDefaultAsync(a => a.Name == "Main", ct);
            if (area is null)
            {
                area = new Area { Name = "Main", SortOrder = 10 };
                db.Areas.Add(area);
            }
            var existingTables = await db.Tables.ToListAsync(ct);
            var sort = existingTables.Where(t => t.AreaId == area.Id).Select(t => t.SortOrder).DefaultIfEmpty(0).Max();
            foreach (var t in data.Tables)
            {
                var reference = $"sambapos:entity:{t.Id}";
                var table = existingTables.FirstOrDefault(x => x.ExternalRef == reference)
                            ?? existingTables.FirstOrDefault(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase));
                if (table is not null)
                {
                    table.ExternalRef ??= reference;
                    continue;
                }
                table = new DiningTable { Name = t.Name, AreaId = area.Id, SortOrder = sort += 1, ExternalRef = reference };
                db.Tables.Add(table);
                existingTables.Add(table);
                tables++;
            }
        }

        audit.Write(new AuditRecord(AuditActions.MenuImported, null, null, null, new
        {
            summary = $"SambaPOS import: {created} new items, {priceLists} price lists, {modifierGroups} modifier groups, {tables} tables, {renamed} renamed, {hidden} hidden",
        }, grant.ApprovedByUserId));
        await MenuAccess.Save(db, ct);

        var newPeople = new List<NewStaffPin>();
        if (options.Users)
        {
            var roles = await db.Roles.Where(x => x.SystemKey != null).ToDictionaryAsync(x => x.SystemKey!, ct);
            var toAdd = plan.Preview.Users.Where(u => u.Status == "New" && u.Role is not null)
                .Select(u => (u.Name, roles[u.Role!], (string?)$"sambapos:user:{u.Id}")).ToList();
            newPeople = await people.Create(toAdd, ct);
        }
        await tx.CommitAsync(ct);
        await access.Changed(ct);
        if (tables > 0) await events.Publish(PosEventNames.TablesChanged, new { }, ct);

        var summary = $"Imported {created} items in {plan.Categories.Count} categories, {lists.Count} price lists, " +
                      $"{groups.Count} modifier groups, {tables} tables, {newPeople.Count} staff." +
                      (renamed + hidden > 0 ? $" Renamed {renamed} items to their plain names and hid {hidden} items from other portions." : "");
        var business = (await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessName);
        return new SambaImportResult(summary, newPeople, newPeople.Count > 0 ? Convert.ToBase64String(PinSheet.Build(business, newPeople)) : null);
    }

    private sealed record PlannedPrice(string? Tag, decimal Price);

    private sealed record PlannedPortion(
        int MenuItemId, string Reference, string Name, string Category, IReadOnlyList<PlannedPrice> Prices, string? LongName = null);

    private sealed record Planned(
        SambaPreview Preview, IReadOnlyList<PlannedPortion> Portions, IReadOnlyList<string> Categories, IReadOnlyList<string> Skipped);

    private async Task<Planned> Plan(SambaPosData data, SambaImportOptions options, CancellationToken ct)
    {
        var screenCategory = data.ScreenItems.GroupBy(s => s.MenuItemId).ToDictionary(g => g.Key, g => g.First().Category);
        string CategoryOf(SambaMenuItem m) =>
            options.CategoriesFromScreenMenu && screenCategory.TryGetValue(m.Id, out var c) ? c
            : string.IsNullOrWhiteSpace(m.GroupCode) ? "Other" : m.GroupCode.Trim();

        var portions = new List<PlannedPortion>();
        var skipped = new List<string>();
        foreach (var item in data.Items)
        {
            var all = data.Portions.Where(p => p.MenuItemId == item.Id).OrderBy(p => p.Id).ToList();
            var mine = all;
            if (options.FirstPriceOnly && all.Count > 1)
            {
                mine = [all.FirstOrDefault(p => data.Prices.Any(x => x.PortionId == p.Id && x.Price > 0)) ?? all[0]];
                skipped.AddRange(all.Except(mine).Select(p => $"sambapos:portion:{p.Id}"));
            }
            foreach (var portion in mine)
            {
                // One portion: the item's own name. Several: "Item (Portion)".
                var name = mine.Count == 1 ? item.Name.Trim() : $"{item.Name.Trim()} ({portion.Name.Trim()})";
                if (name.Length > MenuItem.MaxNameLength) name = name[..MenuItem.MaxNameLength].TrimEnd();
                var prices = data.Prices.Where(p => p.PortionId == portion.Id && p.Price > 0)
                    .Select(p => new PlannedPrice(string.IsNullOrWhiteSpace(p.PriceTag) ? null : p.PriceTag.Trim(), p.Price)).ToList();
                if (options.FirstPriceOnly && prices.Count > 1) prices = [prices.FirstOrDefault(p => p.Tag is null) ?? prices[0]];
                // The name an import without the option gave it, renamed now.
                var longName = all.Count > 1 && mine.Count == 1 ? $"{item.Name.Trim()} ({portion.Name.Trim()})" : null;
                portions.Add(new PlannedPortion(item.Id, $"sambapos:portion:{portion.Id}", name, CategoryOf(item), prices, longName));
            }
        }

        var existing = await db.MenuItems.AsNoTracking().Select(i => new { i.Name, i.ExternalRef }).ToListAsync(ct);
        bool Exists(PlannedPortion p) =>
            existing.Any(e => e.ExternalRef == p.Reference || string.Equals(e.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        var previewItems = options.Menu
            ? portions.Select(p => new SambaPreviewItem(p.Name, p.Category, Exists(p) ? "Existing" : "New",
                p.Prices.Select(x => $"{x.Tag ?? PriceList.StandardName}: {MenuCsv.Money(x.Price)}").ToList())).ToList()
            : [];

        var existingTables = await db.Tables.AsNoTracking().Select(t => new { t.Name, t.ExternalRef }).ToListAsync(ct);
        var newTables = options.Tables
            ? data.Tables.Count(t => !existingTables.Any(e => e.ExternalRef == $"sambapos:entity:{t.Id}" || string.Equals(e.Name, t.Name, StringComparison.OrdinalIgnoreCase)))
            : 0;

        var existingUsers = await db.Users.AsNoTracking().Select(u => new { u.NormalizedName, u.ExternalRef }).ToListAsync(ct);
        var users = options.Users
            ? data.Users.Select(u =>
            {
                var chosen = options.UserRoles?.GetValueOrDefault(u.Id.ToString()) ?? RoleFor(u.Role);
                var known = existingUsers.Any(e => e.ExternalRef == $"sambapos:user:{u.Id}" || e.NormalizedName == User.Normalize(u.Name));
                return new SambaPreviewUser(u.Id.ToString(), u.Name.Trim(), u.Role, chosen is "" ? null : chosen,
                    known ? "Existing" : chosen is null or "" ? "Choose a role" : "New");
            }).ToList()
            : [];

        var categories = portions.Select(p => p.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        var priceTags = portions.SelectMany(p => p.Prices).Select(p => p.Tag ?? PriceList.StandardName).Distinct().Order().ToList();
        var preview = new SambaPreview(previewItems, categories, options.Prices ? priceTags : [],
            options.Modifiers ? data.TagGroups.Select(g => $"{g.Name} ({string.Join(" / ", data.Tags.Where(t => t.GroupId == g.Id).Select(t => t.Name))})").ToList() : [],
            options.Tables ? data.Tables.Select(t => t.Name).ToList() : [], users,
            previewItems.Count(i => i.Status == "New"), previewItems.Count(i => i.Status == "Existing"), newTables,
            users.Count(u => u.Status == "New"), data.Problems);
        return new Planned(preview, portions, categories, skipped);
    }

    /// <summary>SambaPOS role names to ours; anything else is left for the owner to choose.</summary>
    public static string? RoleFor(string? sambaRole)
    {
        var r = (sambaRole ?? "").ToLowerInvariant();
        if (r.Contains("admin")) return RoleKeys.Admin;
        if (r.Contains("manager") || r.Contains("supervisor")) return RoleKeys.Supervisor;
        if (r.Contains("cashier")) return RoleKeys.Cashier;
        if (r.Contains("waiter") || r.Contains("waitress") || r.Contains("barman") || r.Contains("bartender")) return RoleKeys.Waiter;
        return null;
    }
}
