using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Auditing;
using Nexagen.Application.Common;
using Nexagen.Application.Security;
using Nexagen.Domain.Common;
using Nexagen.Domain.People;

namespace Nexagen.Application.Staff;

/// <summary>The person using the staff screen, with their tier and the grant that let them in.</summary>
public sealed record StaffActor(SessionUser User, StaffTier Tier, PermissionGrant Grant);

/// <summary>
/// Shared checks for every staff action: staff.manage is required, and who may manage whom
/// follows <see cref="StaffRules"/>. Role tiers come from data (built-in key, or staff.manage
/// for added roles), never from role names.
/// </summary>
public sealed class StaffAccess(
    INexagenDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval)
{
    public const int MinNameLength = 2;
    public const int MaxNameLength = 40;
    public const int MinPasswordLength = 10;

    public async Task<StaffActor> RequireActor(CancellationToken ct)
    {
        var user = session.RequireUser();
        var grant = await permissions.Require(user, PermissionKeys.StaffManage, approval.ApprovalToken, ct);
        var tiers = await RoleTiers(ct);
        return new StaffActor(user, tiers.GetValueOrDefault(user.RoleId, StaffTier.Staff), grant);
    }

    public async Task<Dictionary<Guid, StaffTier>> RoleTiers(CancellationToken ct)
    {
        var managing = await db.RolePermissions.AsNoTracking()
            .Where(p => p.PermissionKey == PermissionKeys.StaffManage && p.Value != PermissionValue.NotAllowed)
            .Select(p => p.RoleId)
            .ToListAsync(ct);
        var roles = await db.Roles.AsNoTracking().Select(r => new { r.Id, r.SystemKey }).ToListAsync(ct);
        return roles.ToDictionary(r => r.Id, r => StaffRules.TierOf(r.SystemKey, managing.Contains(r.Id)));
    }

    /// <summary>Loads a person the actor may manage, or throws (404 if they may not even see them).</summary>
    public async Task<Domain.People.User> LoadManageable(StaffActor actor, Guid userId, long rowVersion, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("Staff member not found.");
        var tiers = await RoleTiers(ct);
        var targetTier = tiers.GetValueOrDefault(user.RoleId, StaffTier.Staff);
        if (!StaffRules.CanManage(actor.Tier, targetTier))
        {
            if (actor.Tier == StaffTier.Manager && targetTier != StaffTier.Staff && user.Id != actor.User.UserId)
                throw new NotFoundException("Staff member not found.");
            throw new ForbiddenException("You can only manage waiters and cashiers.", StaffCodes.NotManageable);
        }
        if (user.RowVersion != rowVersion)
            throw new ConflictException(StaffCodes.StaleMessage, StaffCodes.Stale);
        // Also let the database catch an edit saved between this read and our save.
        ((DbContext)db).Entry(user).Property(u => u.RowVersion).OriginalValue = rowVersion;
        return user;
    }

    public async Task<Role> RequireAssignableRole(StaffActor actor, Guid roleId, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new DomainException("Choose a role.");
        var tiers = await RoleTiers(ct);
        if (!StaffRules.CanAssign(actor.Tier, tiers.GetValueOrDefault(role.Id, StaffTier.Staff)))
            throw new ForbiddenException("You can't give someone that role.", StaffCodes.RoleNotAssignable);
        return role;
    }

    public async Task<string> ValidName(string? name, Guid? exceptUserId, CancellationToken ct)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is < MinNameLength or > MaxNameLength)
            throw new DomainException($"The name must be {MinNameLength} to {MaxNameLength} characters.", StaffCodes.NameInvalid);
        var normalized = Domain.People.User.Normalize(trimmed);
        // Names stay taken after someone is deactivated, so history never points at two people.
        if (await db.Users.AnyAsync(u => u.NormalizedName == normalized && u.Id != exceptUserId, ct))
            throw new DomainException($"There is already someone called {trimmed}. Use a different name.", StaffCodes.NameTaken);
        return trimmed;
    }

    public static void CheckPassword(string password)
    {
        if (password.Length < MinPasswordLength)
            throw new DomainException($"The admin-site password must be at least {MinPasswordLength} characters.", StaffCodes.PasswordTooShort);
    }

    /// <summary>The admin-site sign-in name: the person's name in lower case, made unique.</summary>
    public async Task<string> NewUserName(string name, CancellationToken ct)
    {
        var baseName = string.Concat(name.Trim().ToLowerInvariant().Select(c => char.IsWhiteSpace(c) ? '.' : c));
        var candidate = baseName;
        for (var n = 2; await db.Users.AnyAsync(u => u.UserName == candidate, ct); n++)
            candidate = $"{baseName}{n}";
        return candidate;
    }

    public async Task EnsureNotLastAdmin(Domain.People.User target, CancellationToken ct)
    {
        var tiers = await RoleTiers(ct);
        if (tiers.GetValueOrDefault(target.RoleId) != StaffTier.Admin || !target.Active) return;
        var adminRoles = tiers.Where(t => t.Value == StaffTier.Admin).Select(t => t.Key).ToList();
        var others = await db.Users.CountAsync(u => u.Active && u.Id != target.Id && adminRoles.Contains(u.RoleId), ct);
        if (others == 0)
            throw new DomainException("There must be at least one active Admin.", StaffCodes.LastAdmin);
    }

    /// <summary>Old and new values for the audit log. Never contains a PIN or a password.</summary>
    public static object AuditView(Domain.People.User u, string roleName) => new
    {
        name = u.Name,
        role = roleName,
        active = u.Active,
        mustChangePin = u.MustChangePin,
        adminUserName = u.UserName,
        hasAdminPassword = u.PasswordHash is not null,
    };

    public static AuditRecord Audit(string action, Domain.People.User u, object? before, object? after, StaffActor actor) =>
        new(action, "User", u.Id.ToString(), before, after, actor.Grant.ApprovedByUserId);
}
