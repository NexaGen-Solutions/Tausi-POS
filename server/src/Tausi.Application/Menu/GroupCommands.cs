using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;

namespace Tausi.Application.Menu;

/// <summary>
/// Adds or edits an option group, its options (names, order, active) and where it applies
/// (categories and items). Option add-ons are prices, set with <see cref="SetOptionPrices"/>.
/// </summary>
public sealed class SaveGroup(ITausiDb db, MenuAccess access, IAuditLog audit)
{
    public async Task<SavedRef> Execute(Guid? id, GroupRequest request, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        var name = MenuAccess.ValidName(request.Name, 2, 60, "option group");
        var upper = name.ToUpperInvariant();
        if (await db.ModifierGroups.AnyAsync(g => g.Name.ToUpper() == upper && g.Id != id, ct))
            throw new DomainException($"There is already an option group called {name}.", MenuCodes.NameTaken);

        var options = request.Options ?? [];
        var optionNames = options.Select(o => MenuAccess.ValidName(o.Name, 1, 60, "option")).ToList();
        if (optionNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != optionNames.Count)
            throw new DomainException("Each option needs a different name.", MenuCodes.GroupInvalid);
        if (ModifierGroup.Problem(request.MinSelect, request.MaxSelect, options.Count(o => o.Active)) is { } problem)
            throw new DomainException(problem, MenuCodes.GroupInvalid);

        ModifierGroup group;
        object? before = null;
        if (id is { } existing)
        {
            group = await db.ModifierGroups.Include(g => g.Options).FirstOrDefaultAsync(g => g.Id == existing, ct)
                ?? throw new NotFoundException("Option group not found.");
            MenuAccess.CheckVersion(group, request.RowVersion, (DbContext)db);
            before = View(group);
        }
        else
        {
            group = new ModifierGroup();
            db.ModifierGroups.Add(group);
        }
        group.Name = name;
        group.MinSelect = request.MinSelect;
        group.MaxSelect = request.MaxSelect;
        group.SortOrder = request.SortOrder;
        group.Active = request.Active;

        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            var option = o.Id is { } oid ? group.Options.FirstOrDefault(x => x.Id == oid)
                ?? throw new DomainException("Unknown option.", MenuCodes.GroupInvalid) : null;
            if (option is null)
            {
                option = new ModifierOption { GroupId = group.Id };
                group.Options.Add(option);
                db.ModifierOptions.Add(option);
            }
            option.Name = optionNames[i];
            option.SortOrder = o.SortOrder;
            option.Active = o.Active;
        }
        // Options left out of the request are kept but deactivated, never deleted.
        foreach (var gone in group.Options.Where(x => options.All(o => o.Id != x.Id)).ToList())
            if (((DbContext)db).Entry(gone).State != EntityState.Added) gone.Active = false;

        await SetAssignments(group.Id, request.CategoryIds ?? [], request.ItemIds ?? [], ct);

        audit.Write(new AuditRecord(AuditActions.MenuGroupChanged, nameof(ModifierGroup), group.Id.ToString(),
            before, View(group), grant.ApprovedByUserId));
        await MenuAccess.Save(db, ct);
        await access.Changed(ct);
        return new SavedRef(group.Id, group.RowVersion);
    }

    private async Task SetAssignments(Guid groupId, IReadOnlyList<Guid> categoryIds, IReadOnlyList<Guid> itemIds, CancellationToken ct)
    {
        var cats = categoryIds.Distinct().ToList();
        var items = itemIds.Distinct().ToList();
        if (await db.Categories.CountAsync(c => cats.Contains(c.Id), ct) != cats.Count
            || await db.MenuItems.CountAsync(i => items.Contains(i.Id), ct) != items.Count)
            throw new DomainException("Unknown category or item.", MenuCodes.GroupInvalid);

        var current = await db.ModifierGroupAssignments.Where(a => a.GroupId == groupId).ToListAsync(ct);
        db.ModifierGroupAssignments.RemoveRange(current.Where(a =>
            (a.CategoryId is { } c && !cats.Contains(c)) || (a.MenuItemId is { } i && !items.Contains(i))));
        foreach (var c in cats.Where(c => current.All(a => a.CategoryId != c)))
            db.ModifierGroupAssignments.Add(new ModifierGroupAssignment { GroupId = groupId, CategoryId = c });
        foreach (var i in items.Where(i => current.All(a => a.MenuItemId != i)))
            db.ModifierGroupAssignments.Add(new ModifierGroupAssignment { GroupId = groupId, MenuItemId = i });
    }

    private static object View(ModifierGroup g) => new
    {
        g.Name, g.MinSelect, g.MaxSelect, g.Active,
        options = g.Options.OrderBy(o => o.SortOrder).Select(o => new { o.Name, o.Active }),
    };
}

/// <summary>Sets option add-ons (menu.edit_prices), logged like item prices.</summary>
public sealed class SetOptionPrices(ITausiDb db, MenuAccess access, IAuditLog audit, TimeProvider clock)
{
    public async Task<int> Execute(Guid groupId, OptionPricesRequest request, CancellationToken ct)
    {
        var grant = await access.RequirePrices(ct);
        var group = await db.ModifierGroups.Include(g => g.Options).FirstOrDefaultAsync(g => g.Id == groupId, ct)
            ?? throw new NotFoundException("Option group not found.");
        MenuAccess.CheckVersion(group, request.RowVersion, (DbContext)db);

        var now = clock.GetUtcNow();
        var changes = new List<object>();
        foreach (var (optionId, addOn) in request.AddOns)
        {
            var option = group.Options.FirstOrDefault(o => o.Id == optionId)
                ?? throw new DomainException("Unknown option.", MenuCodes.GroupInvalid);
            SetItemPrices.CheckPrice(addOn, allowZero: true);
            if (option.PriceAddOn == addOn) continue;
            db.PriceChangeLogs.Add(new PriceChangeLog
            {
                ModifierOptionId = option.Id, OldPrice = option.PriceAddOn, NewPrice = addOn,
                ChangedByUserId = access.User.UserId, ApprovedByUserId = grant.ApprovedByUserId,
                TerminalId = access.TerminalId, Source = PriceChangeSources.Admin, At = now,
            });
            changes.Add(new { option = option.Name, old = option.PriceAddOn, @new = addOn });
            option.PriceAddOn = addOn;
        }
        if (changes.Count == 0) return 0;

        ((DbContext)db).Entry(group).State = EntityState.Modified;
        audit.Write(new AuditRecord(AuditActions.MenuPricesChanged, nameof(ModifierGroup), group.Id.ToString(),
            null, new { group = group.Name, changes }, grant.ApprovedByUserId));
        await MenuAccess.Save(db, ct);
        await access.Changed(ct);
        return changes.Count;
    }
}
