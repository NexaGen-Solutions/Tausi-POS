using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.People;

namespace Tausi.Application.Security;

/// <summary>Permissions are data: the role x key grid in <c>RolePermissions</c>. A missing cell means NotAllowed.</summary>
public sealed class PermissionService(ITausiDb db, TimeProvider clock) : IPermissionService
{
    public async Task<PermissionValue> Check(SessionUser user, string permissionKey, CancellationToken ct = default) =>
        await CheckRole(user.RoleId, permissionKey, ct);

    public async Task<PermissionGrant> Require(
        SessionUser user, string permissionKey, string? approvalToken = null, CancellationToken ct = default)
    {
        switch (await CheckRole(user.RoleId, permissionKey, ct))
        {
            case PermissionValue.Allowed:
                return new PermissionGrant(permissionKey, null);
            case PermissionValue.NotAllowed:
                throw new PermissionDeniedException(permissionKey);
        }

        if (string.IsNullOrWhiteSpace(approvalToken))
            throw new ApprovalRequiredException(permissionKey);

        var now = clock.GetUtcNow();
        var hash = SecureTokens.Hash(approvalToken);
        var grant = await db.ApprovalGrants.AsNoTracking().FirstOrDefaultAsync(g => g.TokenHash == hash, ct);
        if (grant is null || !grant.IsUsable(permissionKey, now))
            throw new ApprovalRequiredException(permissionKey, "The approval has expired or was already used. Ask again.");

        // Nobody approves their own action, with a PIN or otherwise.
        if (grant.ApprovedByUserId == user.UserId)
            throw new ApprovalRequiredException(permissionKey, "You can't approve your own action. Ask a supervisor.");

        // The approver must still be active and still allowed to do this themselves.
        var approver = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == grant.ApprovedByUserId, ct);
        if (approver is null || !approver.Active || await CheckRole(approver.RoleId, permissionKey, ct) != PermissionValue.Allowed)
            throw new ApprovalRequiredException(permissionKey, "The person who approved this can no longer approve it.");

        // Use the token up in one conditional update, so two requests cannot both use it.
        var used = await db.ApprovalGrants
            .Where(g => g.Id == grant.Id && g.UsedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.UsedAt, now)
                .SetProperty(g => g.UsedByUserId, user.UserId), ct);
        if (used != 1)
            throw new ApprovalRequiredException(permissionKey, "The approval has expired or was already used. Ask again.");

        return new PermissionGrant(permissionKey, grant.ApprovedByUserId);
    }

    public async Task<Guid> RequireSignOff(string permissionKey, string? approvalToken, Guid notUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(approvalToken)) throw new ApprovalRequiredException(permissionKey);
        var now = clock.GetUtcNow();
        var hash = SecureTokens.Hash(approvalToken);
        var grant = await db.ApprovalGrants.AsNoTracking().FirstOrDefaultAsync(g => g.TokenHash == hash, ct);
        if (grant is null || !grant.IsUsable(permissionKey, now))
            throw new ApprovalRequiredException(permissionKey, "The sign-off has expired or was already used. Ask again.");
        if (grant.ApprovedByUserId == notUserId)
            throw new ApprovalRequiredException(permissionKey, "You can't sign off your own shift. Ask a supervisor.");
        var approver = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == grant.ApprovedByUserId, ct);
        if (approver is null || !approver.Active || await CheckRole(approver.RoleId, permissionKey, ct) != PermissionValue.Allowed)
            throw new ApprovalRequiredException(permissionKey, "The person who signed off can no longer do so.");
        var used = await db.ApprovalGrants.Where(g => g.Id == grant.Id && g.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.UsedAt, now).SetProperty(g => g.UsedByUserId, notUserId), ct);
        if (used != 1) throw new ApprovalRequiredException(permissionKey, "The sign-off has expired or was already used. Ask again.");
        return grant.ApprovedByUserId;
    }

    /// <summary>Actions a supervisor can't approve for someone else: NeedsApproval counts as NotAllowed.</summary>
    public static readonly IReadOnlySet<string> NotApprovable = new HashSet<string> { PermissionKeys.TicketSettle };

    private async Task<PermissionValue> CheckRole(Guid roleId, string permissionKey, CancellationToken ct)
    {
        if (!PermissionKeys.IsKnown(permissionKey))
            throw new ArgumentException($"Unknown permission key '{permissionKey}'.", nameof(permissionKey));

        var cell = await db.RolePermissions.AsNoTracking()
            .Where(p => p.RoleId == roleId && p.PermissionKey == permissionKey)
            .Select(p => (PermissionValue?)p.Value)
            .FirstOrDefaultAsync(ct);
        // Only the cashier settles: a waiter can't settle even with a supervisor's PIN.
        if (cell == PermissionValue.NeedsApproval && NotApprovable.Contains(permissionKey)) return PermissionValue.NotAllowed;
        return cell ?? PermissionValue.NotAllowed;
    }
}
