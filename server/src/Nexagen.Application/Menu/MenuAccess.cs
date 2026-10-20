using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Common;
using Nexagen.Application.Events;
using Nexagen.Domain.Common;
using Nexagen.Domain.Menu;
using Nexagen.Domain.People;
using Nexagen.Application.Security;

namespace Nexagen.Application.Menu;

/// <summary>
/// Menu permissions. Viewing the menu admin needs menu.edit or menu.edit_prices; structure changes
/// need menu.edit; any price (item prices, option add-ons) needs menu.edit_prices, with the usual
/// approval token when it is NeedsApproval.
/// </summary>
public sealed class MenuAccess(
    INexagenDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IPosEvents events)
{
    public SessionUser User => session.RequireUser();

    public async Task<(bool CanEdit, bool CanEditPrices)> RequireView(CancellationToken ct)
    {
        var user = session.RequireUser();
        var edit = await permissions.Check(user, PermissionKeys.MenuEdit, ct);
        var prices = await permissions.Check(user, PermissionKeys.MenuEditPrices, ct);
        if (edit == PermissionValue.NotAllowed && prices == PermissionValue.NotAllowed)
            throw new PermissionDeniedException(PermissionKeys.MenuEdit);
        return (edit != PermissionValue.NotAllowed, prices != PermissionValue.NotAllowed);
    }

    public Task<PermissionGrant> RequireEdit(CancellationToken ct) =>
        permissions.Require(session.RequireUser(), PermissionKeys.MenuEdit, approval.ApprovalToken, ct);

    public Task<PermissionGrant> RequirePrices(CancellationToken ct) =>
        permissions.Require(session.RequireUser(), PermissionKeys.MenuEditPrices, approval.ApprovalToken, ct);

    public Task Changed(CancellationToken ct) => events.Publish(PosEventNames.MenuChanged, new { }, ct);

    /// <summary>Where a change was made: the terminal, or null for the admin site.</summary>
    public Guid? TerminalId => session.User?.TerminalId;

    public static void CheckVersion(VersionedEntity entity, long rowVersion, DbContext db)
    {
        if (entity.RowVersion != rowVersion) throw new ConflictException(MenuCodes.StaleMessage, MenuCodes.Stale);
        db.Entry(entity).Property(e => e.RowVersion).OriginalValue = rowVersion;
    }

    public static string ValidName(string? name, int min, int max, string what)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length < min || trimmed.Length > max)
            throw new DomainException($"The {what} name must be {min} to {max} characters.", MenuCodes.NameInvalid);
        return trimmed;
    }

    public static void CheckVat(decimal? rate)
    {
        if (rate is < 0m or >= 100m)
            throw new DomainException("The VAT rate must be from 0 to below 100.", MenuCodes.VatInvalid);
    }

    public async Task<PriceList> DefaultList(CancellationToken ct) =>
        await db.PriceLists.FirstOrDefaultAsync(l => l.IsDefault, ct)
            ?? throw new ConflictException("There is no default price list.");

    /// <summary>Saves; races the database catches become clear answers.</summary>
    public static async Task Save(INexagenDb db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(MenuCodes.StaleMessage, MenuCodes.Stale);
        }
        catch (DbUpdateException)
        {
            throw new DomainException("That name is already used. Choose another.", MenuCodes.NameTaken);
        }
    }
}
