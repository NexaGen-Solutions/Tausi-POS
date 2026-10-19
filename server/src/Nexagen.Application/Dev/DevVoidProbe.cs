using Nexagen.Application.Abstractions;
using Nexagen.Application.Auditing;
using Nexagen.Application.Security;
using Nexagen.Domain.People;

namespace Nexagen.Application.Dev;

public sealed record DevVoidProbeResult(Guid DoneBy, Guid? ApprovedBy);

/// <summary>
/// TEMPORARY, test-only: an action guarded by <c>order.void</c>, used to prove the approval flow
/// before the real void exists. Mapped only in Development and Testing. Remove with the void task.
/// </summary>
public sealed class DevVoidProbe(
    INexagenDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval, IAuditLog audit)
{
    public async Task<DevVoidProbeResult> Execute(CancellationToken ct)
    {
        var user = session.RequireUser();
        var grant = await permissions.Require(user, PermissionKeys.OrderVoid, approval.ApprovalToken, ct);
        audit.Write(new AuditRecord(AuditActions.DevVoidProbe, ApprovedByUserId: grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        return new DevVoidProbeResult(user.UserId, grant.ApprovedByUserId);
    }
}
