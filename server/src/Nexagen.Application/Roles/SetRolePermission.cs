using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Auditing;
using Nexagen.Application.Common;
using Nexagen.Application.Security;
using Nexagen.Domain.Common;
using Nexagen.Domain.People;

namespace Nexagen.Application.Roles;

public sealed record RolePermissionRequest(string PermissionKey, PermissionValue Value);

/// <summary>
/// Changes one cell of the role x permission grid (permissions.manage), audited with old and new
/// values. API only until the Permissions screen exists.
/// </summary>
public sealed class SetRolePermission(
    INexagenDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval, IAuditLog audit)
{
    public async Task Execute(Guid roleId, RolePermissionRequest request, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.PermissionsManage, approval.ApprovalToken, ct);
        if (!PermissionKeys.IsKnown(request.PermissionKey ?? "")) throw new DomainException("Unknown permission.");
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct) ?? throw new NotFoundException("Role not found.");

        var cell = await db.RolePermissions.FirstOrDefaultAsync(p => p.RoleId == roleId && p.PermissionKey == request.PermissionKey, ct);
        var old = cell?.Value ?? PermissionValue.NotAllowed;
        if (cell is null)
            db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionKey = request.PermissionKey!, Value = request.Value });
        else
            cell.Value = request.Value;

        audit.Write(new AuditRecord(AuditActions.PermissionChanged, "Role", role.Id.ToString(),
            new { role = role.Name, permission = request.PermissionKey, value = old },
            new { role = role.Name, permission = request.PermissionKey, value = request.Value },
            grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
    }
}
