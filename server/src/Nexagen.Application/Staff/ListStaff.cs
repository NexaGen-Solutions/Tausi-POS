using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Common;
using Nexagen.Domain.People;

namespace Nexagen.Application.Staff;

/// <summary>
/// The staff list. Admins see everyone; managers (Supervisors) see only the people they may
/// manage. Active people first, then inactive, each by name.
/// </summary>
public sealed class ListStaff(INexagenDb db, StaffAccess access, TimeProvider clock)
{
    public async Task<StaffListDto> Execute(CancellationToken ct)
    {
        var actor = await access.RequireActor(ct);
        var tiers = await access.RoleTiers(ct);
        var visible = (await db.Users.AsNoTracking().Include(u => u.Role).ToListAsync(ct))
            .Where(u => StaffRules.CanManage(actor.Tier, tiers.GetValueOrDefault(u.RoleId, StaffTier.Staff)))
            .OrderByDescending(u => u.Active)
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var roles = (await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct))
            .Where(r => StaffRules.CanAssign(actor.Tier, tiers.GetValueOrDefault(r.Id, StaffTier.Staff)))
            .Select(r => new StaffRoleDto(r.Id, r.Name, r.CanUseAdminSite))
            .ToList();

        return new StaffListDto(await StaffQueries.Rows(db, visible, clock.GetUtcNow(), ct), roles);
    }
}

public sealed class GetStaffMember(INexagenDb db, StaffAccess access, TimeProvider clock)
{
    public async Task<StaffMemberDto> Execute(Guid id, CancellationToken ct)
    {
        var actor = await access.RequireActor(ct);
        var tiers = await access.RoleTiers(ct);
        var user = await db.Users.AsNoTracking().Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null || !StaffRules.CanManage(actor.Tier, tiers.GetValueOrDefault(user.RoleId, StaffTier.Staff)))
            throw new NotFoundException("Staff member not found.");
        return (await StaffQueries.Rows(db, [user], clock.GetUtcNow(), ct))[0];
    }
}
