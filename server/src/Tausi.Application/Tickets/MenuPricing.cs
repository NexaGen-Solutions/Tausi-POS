using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Menu;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;

namespace Tausi.Application.Tickets;

/// <summary>What a line copies from the menu when it is added.</summary>
public sealed record PricedItem(
    Guid MenuItemId, string Name, string ShortName, decimal UnitPrice, decimal VatRate, string? Options, string OptionIds);

/// <summary>
/// Prices items for one terminal: the price comes from the terminal's price list (or the default
/// list for items it doesn't price), plus any option add-ons, never from the request. It checks
/// the chosen options against the item's groups: each group's count must be within its minimum
/// and maximum, so a required choice (e.g. Cold / Warm) can't be skipped.
/// </summary>
public sealed class MenuPricing(ITausiDb db)
{
    public async Task<Pricer> For(Guid terminalId, CancellationToken ct)
    {
        var terminal = await db.Terminals.AsNoTracking().FirstAsync(t => t.Id == terminalId, ct);
        var lists = await db.PriceLists.AsNoTracking().ToListAsync(ct);
        var defaultList = lists.FirstOrDefault(l => l.IsDefault) ?? throw new ConflictException("There is no default price list.");
        var list = lists.FirstOrDefault(l => l.Id == terminal.PriceListId && l.Active) ?? defaultList;
        var vat = (await db.BusinessSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct))?.VatRate ?? 16m;
        var groups = await db.ModifierGroups.AsNoTracking().Include(g => g.Options).Where(g => g.Active).ToDictionaryAsync(g => g.Id, ct);
        var assignments = await db.ModifierGroupAssignments.AsNoTracking().ToListAsync(ct);
        return new Pricer(db, list.Id, defaultList.Id, vat, groups, assignments);
    }
}

public sealed class Pricer(
    ITausiDb db, Guid listId, Guid defaultListId, decimal businessVat,
    IReadOnlyDictionary<Guid, ModifierGroup> groups, IReadOnlyCollection<ModifierGroupAssignment> assignments)
{
    public async Task<PricedItem> Price(Guid menuItemId, IReadOnlyCollection<Guid>? optionIds, CancellationToken ct)
    {
        var item = await db.MenuItems.AsNoTracking().Include(i => i.Prices).FirstOrDefaultAsync(i => i.Id == menuItemId, ct);
        if (item is null || !item.Active) throw new DomainException("That item is no longer on the menu.");
        var price = MenuRules.PriceOn(item, listId) ?? MenuRules.PriceOn(item, defaultListId)
            ?? throw new DomainException($"{item.Name} has no price on this terminal's price list.");

        var (own, inherited) = MenuRules.GroupsOf(item, assignments);
        var itemGroups = inherited.Concat(own).Distinct().Where(groups.ContainsKey).Select(g => groups[g])
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToList();
        var chosen = (optionIds ?? []).Distinct().ToList();
        var picked = new List<(ModifierGroup Group, ModifierOption Option)>();
        foreach (var id in chosen)
        {
            var match = itemGroups.SelectMany(g => g.Options.Where(o => o.Active && o.Id == id).Select(o => (g, o))).FirstOrDefault();
            if (match.o is null) throw new DomainException($"That choice isn't available for {item.Name}.");
            picked.Add(match);
        }
        foreach (var group in itemGroups)
        {
            var count = picked.Count(p => p.Group.Id == group.Id);
            if (count < group.MinSelect || count > group.MaxSelect)
                throw new DomainException($"{item.Name}: {group.Name} · {ModifierGroup.ChoiceText(group.MinSelect, group.MaxSelect)}.");
        }

        var ordered = picked.OrderBy(p => p.Group.SortOrder).ThenBy(p => p.Group.Name).ThenBy(p => p.Option.SortOrder).ThenBy(p => p.Option.Name).ToList();
        var text = ordered.Count == 0 ? null : string.Join(", ", ordered.Select(p => p.Option.Name));
        var ids = string.Join(",", chosen.Order());
        return new PricedItem(item.Id, item.Name, item.ShortName, price + ordered.Sum(p => p.Option.PriceAddOn),
            item.EffectiveVatRate(businessVat), text, ids);
    }
}
