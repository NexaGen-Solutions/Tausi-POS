using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;

namespace Nexagen.Application.Menu;

/// <summary>
/// The whole menu as CSV: category, name, short_name, vat_rate, active, one column per price list
/// (default first), option_groups (the item's own groups, separated by ";"), erpnext_item_code.
/// Importing an unchanged export changes nothing.
/// </summary>
public sealed class ExportMenu(INexagenDb db, MenuAccess access)
{
    public async Task<string> Execute(CancellationToken ct)
    {
        await access.RequireView(ct);
        var lists = await db.PriceLists.AsNoTracking().OrderByDescending(l => l.IsDefault).ThenBy(l => l.Name).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var items = await db.MenuItems.AsNoTracking().Include(i => i.Prices).ToListAsync(ct);
        var groups = await db.ModifierGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        var assignments = await db.ModifierGroupAssignments.AsNoTracking().Where(a => a.MenuItemId != null).ToListAsync(ct);

        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "category", "name", "short_name", "vat_rate", "active" }
                .Concat(lists.Select(l => l.Name)).Concat(["option_groups", "erpnext_item_code"]).ToList(),
        };
        foreach (var i in items.OrderBy(i => categories[i.CategoryId].SortOrder).ThenBy(i => i.SortOrder).ThenBy(i => i.Name))
        {
            rows.Add(new[]
                {
                    categories[i.CategoryId].Name, i.Name, i.ShortName,
                    i.VatRate is { } v ? MenuCsv.Money(v) : "", i.Active ? "yes" : "no",
                }
                .Concat(lists.Select(l => MenuRules.PriceOn(i, l.Id) is { } p ? MenuCsv.Money(p) : ""))
                .Concat([
                    string.Join("; ", assignments.Where(a => a.MenuItemId == i.Id).Select(a => groups[a.GroupId]).Order()),
                    i.ErpItemCode ?? "",
                ]).ToList());
        }
        return MenuCsv.Write(rows);
    }
}
