using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Domain.Common;
using Tausi.Domain.Menu;

namespace Tausi.Application.Menu;

/// <summary>Adds, renames or (de)activates a price list. The default list can't be deactivated.</summary>
public sealed class SavePriceList(ITausiDb db, MenuAccess access, IAuditLog audit)
{
    public async Task<AdminPriceListDto> Execute(Guid? id, PriceListRequest request, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        var name = MenuAccess.ValidName(request.Name, 2, 40, "price list");
        var upper = name.ToUpperInvariant();
        if (await db.PriceLists.AnyAsync(l => l.Name.ToUpper() == upper && l.Id != id, ct))
            throw new DomainException($"There is already a price list called {name}.", MenuCodes.NameTaken);

        PriceList list;
        object? before = null;
        if (id is { } existing)
        {
            list = await db.PriceLists.FirstOrDefaultAsync(l => l.Id == existing, ct)
                ?? throw new NotFoundException("Price list not found.");
            MenuAccess.CheckVersion(list, request.RowVersion, (DbContext)db);
            if (list.IsDefault && !request.Active)
                throw new DomainException("The default price list can't be deactivated. Make another list the default first.");
            before = new { list.Name, list.Active };
        }
        else
        {
            list = new PriceList();
            db.PriceLists.Add(list);
        }
        list.Name = name;
        list.Active = request.Active;

        audit.Write(new AuditRecord(AuditActions.MenuPriceListChanged, nameof(PriceList), list.Id.ToString(),
            before, new { list.Name, list.Active }, grant.ApprovedByUserId));
        await MenuAccess.Save(db, ct);
        await access.Changed(ct);
        return new AdminPriceListDto(list.Id, list.Name, list.Active, list.IsDefault, list.RowVersion);
    }
}

/// <summary>
/// Makes a list the default. Exactly one default at any time: the old one is cleared and the new
/// one set in one transaction (the database also allows only one).
/// </summary>
public sealed class MakeDefaultPriceList(ITausiDb db, MenuAccess access, IAuditLog audit)
{
    public async Task Execute(Guid id, CancellationToken ct)
    {
        var grant = await access.RequireEdit(ct);
        var list = await db.PriceLists.FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new NotFoundException("Price list not found.");
        if (list.IsDefault) return;
        if (!list.Active) throw new DomainException("Activate the list before making it the default.");
        var unpriced = await db.MenuItems.CountAsync(i => i.Active && !i.Prices.Any(p => p.PriceListId == id), ct);
        if (unpriced > 0)
            throw new DomainException($"{unpriced} active items have no price on {list.Name}. Every item needs a price on the default list.",
                MenuCodes.DefaultPriceRequired);

        var old = await db.PriceLists.FirstOrDefaultAsync(l => l.IsDefault, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (old is not null)
        {
            old.IsDefault = false;
            await MenuAccess.Save(db, ct);
        }
        list.IsDefault = true;
        audit.Write(new AuditRecord(AuditActions.MenuPriceListChanged, nameof(PriceList), list.Id.ToString(),
            new { defaultList = old?.Name }, new { defaultList = list.Name }, grant.ApprovedByUserId));
        await MenuAccess.Save(db, ct);
        await tx.CommitAsync(ct);
        await access.Changed(ct);
    }
}
