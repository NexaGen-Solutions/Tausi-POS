using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.Menu;

namespace Tausi.Application.Menu;

/// <summary>Effective option groups: an item gets its category's groups plus its own.</summary>
public static class MenuRules
{
    public static (List<Guid> Own, List<Guid> Inherited) GroupsOf(
        MenuItem item, IReadOnlyCollection<ModifierGroupAssignment> assignments)
    {
        var own = assignments.Where(a => a.MenuItemId == item.Id).Select(a => a.GroupId).ToList();
        var inherited = assignments.Where(a => a.CategoryId == item.CategoryId).Select(a => a.GroupId)
            .Where(g => !own.Contains(g)).ToList();
        return (own, inherited);
    }

    public static decimal? PriceOn(MenuItem item, Guid listId) =>
        item.Prices.FirstOrDefault(p => p.PriceListId == listId)?.Price;
}

public sealed class GetAdminMenu(ITausiDb db, MenuAccess access)
{
    public async Task<AdminMenuDto> Execute(CancellationToken ct)
    {
        var (canEdit, canEditPrices) = await access.RequireView(ct);
        var categories = await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var items = await db.MenuItems.AsNoTracking().Include(i => i.Prices).ToListAsync(ct);
        var lists = await db.PriceLists.AsNoTracking().OrderByDescending(l => l.IsDefault).ThenBy(l => l.Name).ToListAsync(ct);
        var groups = await db.ModifierGroups.AsNoTracking().Include(g => g.Options).OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(ct);
        var assignments = await db.ModifierGroupAssignments.AsNoTracking().ToListAsync(ct);
        var business = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        var categoryOrder = categories.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);

        return new AdminMenuDto(
            categories.Select(c => new AdminCategoryDto(c.Id, c.Name, c.SortOrder, c.Active,
                items.Count(i => i.CategoryId == c.Id && i.Active), c.RowVersion, c.ItemsPerPage)).ToList(),
            items.OrderBy(i => categoryOrder.GetValueOrDefault(i.CategoryId)).ThenBy(i => i.SortOrder).ThenBy(i => i.Name)
                .Select(i =>
                {
                    var (own, inherited) = MenuRules.GroupsOf(i, assignments);
                    return new AdminItemDto(i.Id, i.Name, i.ShortName, i.CategoryId, i.SortOrder, i.VatRate, i.Active,
                        i.ErpItemCode, i.Prices.ToDictionary(p => p.PriceListId, p => p.Price), own, inherited, i.RowVersion, i.VatExempt, i.ImageFileId);
                }).ToList(),
            lists.Select(l => new AdminPriceListDto(l.Id, l.Name, l.Active, l.IsDefault, l.RowVersion)).ToList(),
            groups.Select(g => new AdminGroupDto(g.Id, g.Name, g.MinSelect, g.MaxSelect, g.SortOrder, g.Active,
                g.Options.OrderBy(o => o.SortOrder).ThenBy(o => o.Name)
                    .Select(o => new AdminOptionDto(o.Id, o.Name, o.PriceAddOn, o.SortOrder, o.Active)).ToList(),
                assignments.Where(a => a.GroupId == g.Id && a.CategoryId != null).Select(a => a.CategoryId!.Value).ToList(),
                assignments.Where(a => a.GroupId == g.Id && a.MenuItemId != null).Select(a => a.MenuItemId!.Value).ToList(),
                g.RowVersion)).ToList(),
            canEdit, canEditPrices, business?.VatRate ?? 16m, business?.Currency ?? "KES");
    }
}

/// <summary>Who changed which price, when, where, and who approved it. Newest first.</summary>
public sealed class GetPriceLog(ITausiDb db, MenuAccess access)
{
    public async Task<IReadOnlyList<PriceLogEntryDto>> Execute(Guid? itemId, int take, CancellationToken ct)
    {
        await access.RequireView(ct);
        var rows = await db.PriceChangeLogs.AsNoTracking()
            .Where(l => l.Kind == PriceChangeKind.Menu)
            .Where(l => itemId == null || l.MenuItemId == itemId)
            .OrderByDescending(l => l.At)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
        var items = await db.MenuItems.AsNoTracking().ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        var options = await db.ModifierOptions.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Name, ct);
        var lists = await db.PriceLists.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var terminals = await db.Terminals.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return rows.Select(l => new PriceLogEntryDto(
            l.At,
            l.MenuItemId is { } i ? items.GetValueOrDefault(i) : null,
            l.ModifierOptionId is { } o ? options.GetValueOrDefault(o) : null,
            l.PriceListId is { } p ? lists.GetValueOrDefault(p) : null,
            l.OldPrice, l.NewPrice,
            users.GetValueOrDefault(l.ChangedByUserId, "?"),
            l.ApprovedByUserId is { } a ? users.GetValueOrDefault(a) : null,
            l.TerminalId is { } t ? terminals.GetValueOrDefault(t, "?") : "Admin site",
            l.Source)).ToList();
    }
}

/// <summary>
/// The menu a terminal sells: active categories and items, each priced from the terminal's price
/// list (or the default list where it has no price), with the effective VAT rate and option groups.
/// Items without a default price are not sold yet and left out.
/// </summary>
public sealed class GetPosMenu(ITausiDb db, ICurrentSession session)
{
    public async Task<PosMenuDto> Execute(CancellationToken ct)
    {
        var device = session.RequireTerminal();
        var terminal = await db.Terminals.AsNoTracking().FirstAsync(t => t.Id == device.TerminalId, ct);
        var lists = await db.PriceLists.AsNoTracking().ToListAsync(ct);
        var defaultList = lists.FirstOrDefault(l => l.IsDefault)
            ?? throw new ConflictException("There is no default price list.");
        var list = lists.FirstOrDefault(l => l.Id == terminal.PriceListId && l.Active) ?? defaultList;

        var business = await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        var businessVat = business?.VatRate ?? 16m;
        var categories = await db.Categories.AsNoTracking().Where(c => c.Active)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var items = await db.MenuItems.AsNoTracking().Include(i => i.Prices).Where(i => i.Active)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct);
        var groups = await db.ModifierGroups.AsNoTracking().Include(g => g.Options).Where(g => g.Active)
            .ToDictionaryAsync(g => g.Id, ct);
        var assignments = await db.ModifierGroupAssignments.AsNoTracking().ToListAsync(ct);

        PosGroupDto? Group(Guid id) => groups.TryGetValue(id, out var g)
            ? new PosGroupDto(g.Id, g.Name, g.MinSelect, g.MaxSelect,
                g.Options.Where(o => o.Active).OrderBy(o => o.SortOrder).ThenBy(o => o.Name)
                    .Select(o => new PosOptionDto(o.Id, o.Name, o.PriceAddOn, o.IsDefault)).ToList())
            : null;

        var result = new List<PosCategoryDto>();
        foreach (var c in categories)
        {
            var sold = new List<PosItemDto>();
            foreach (var i in items.Where(i => i.CategoryId == c.Id))
            {
                var price = MenuRules.PriceOn(i, list.Id) ?? MenuRules.PriceOn(i, defaultList.Id);
                if (price is null) continue;
                var (own, inherited) = MenuRules.GroupsOf(i, assignments);
                var effective = inherited.Concat(own).Select(Group).OfType<PosGroupDto>()
                    .OrderBy(g => groups[g.Id].SortOrder).ThenBy(g => g.Name).ToList();
                sold.Add(new PosItemDto(i.Id, i.Name, i.ShortName, price.Value, i.EffectiveVatRate(businessVat), effective, i.ImageFileId));
            }
            if (sold.Count > 0) result.Add(new PosCategoryDto(c.Id, c.Name, sold, c.ItemsPerPage));
        }

        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Serialize(new { list.Id, result }))))[..16];
        return new PosMenuDto(version, list.Id, list.Name, result);
    }
}
