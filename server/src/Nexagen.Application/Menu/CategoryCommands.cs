using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Auditing;
using Nexagen.Application.Common;
using Nexagen.Domain.Common;
using Nexagen.Domain.Menu;

namespace Nexagen.Application.Menu;

public sealed class SaveCategory(INexagenDb db, MenuAccess access, IAuditLog audit)
{
    public async Task<AdminCategoryDto> Execute(Guid? id, CategoryRequest request, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        var name = MenuAccess.ValidName(request.Name, 2, 60, "category");
        var upper = name.ToUpperInvariant();
        if (await db.Categories.AnyAsync(c => c.Name.ToUpper() == upper && c.Id != id, ct))
            throw new DomainException($"There is already a category called {name}.", MenuCodes.NameTaken);

        Category category;
        object? before = null;
        if (id is { } existing)
        {
            category = await db.Categories.FirstOrDefaultAsync(c => c.Id == existing, ct)
                ?? throw new NotFoundException("Category not found.");
            MenuAccess.CheckVersion(category, request.RowVersion, (DbContext)db);
            before = new { category.Name, category.Active };
        }
        else
        {
            var last = await db.Categories.MaxAsync(c => (int?)c.SortOrder, ct) ?? 0;
            category = new Category { SortOrder = last + 10 };
            db.Categories.Add(category);
        }
        category.Name = name;
        category.Active = request.Active;

        audit.Write(new AuditRecord(AuditActions.MenuCategoryChanged, nameof(Category), category.Id.ToString(),
            before, new { category.Name, category.Active }, grant.ApprovedByUserId));
        await MenuAccess.Save(db, ct);
        await access.Changed(ct);
        return new AdminCategoryDto(category.Id, category.Name, category.SortOrder, category.Active,
            await db.MenuItems.CountAsync(i => i.CategoryId == category.Id && i.Active, ct), category.RowVersion);
    }
}

/// <summary>Puts categories in the given order (the drag-and-drop list).</summary>
public sealed class ReorderCategories(INexagenDb db, MenuAccess access, IAuditLog audit)
{
    public async Task Execute(ReorderRequest request, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        var categories = await db.Categories.ToListAsync(ct);
        var order = request.Ids.Distinct().ToList();
        if (order.Count != categories.Count || order.Any(id => categories.All(c => c.Id != id)))
            throw new DomainException("Send every category once, in the new order.");
        for (var i = 0; i < order.Count; i++)
            categories.Single(c => c.Id == order[i]).SortOrder = (i + 1) * 10;
        audit.Write(new AuditRecord(AuditActions.MenuCategoryChanged, nameof(Category), null, null,
            new { order = order.Select(id => categories.Single(c => c.Id == id).Name) }, grant.ApprovedByUserId));
        await MenuAccess.Save(db, ct);
        await access.Changed(ct);
    }
}

/// <summary>Deactivates a category; optionally its active items too. Nothing is deleted.</summary>
public sealed class DeactivateCategory(INexagenDb db, MenuAccess access, IAuditLog audit)
{
    public async Task<int> Execute(Guid id, DeactivateCategoryRequest request, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Category not found.");
        MenuAccess.CheckVersion(category, request.RowVersion, (DbContext)db);
        category.Active = false;
        var items = request.IncludeItems
            ? await db.MenuItems.Where(i => i.CategoryId == id && i.Active).ToListAsync(ct)
            : [];
        foreach (var item in items) item.Active = false;

        audit.Write(new AuditRecord(AuditActions.MenuCategoryChanged, nameof(Category), id.ToString(),
            new { active = true }, new { active = false, itemsDeactivated = items.Select(i => i.Name) }, grant.ApprovedByUserId));
        await MenuAccess.Save(db, ct);
        await access.Changed(ct);
        return items.Count;
    }
}
