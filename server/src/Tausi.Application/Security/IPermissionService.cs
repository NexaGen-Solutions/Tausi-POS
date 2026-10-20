using Tausi.Domain.People;

namespace Tausi.Application.Security;

/// <summary>What <see cref="IPermissionService.Require"/> allowed, and who approved it if anyone.</summary>
public sealed record PermissionGrant(string PermissionKey, Guid? ApprovedByUserId)
{
    public bool WasApproved => ApprovedByUserId is not null;
}

public interface IPermissionService
{
    Task<PermissionValue> Check(SessionUser user, string permissionKey, CancellationToken ct = default);

    /// <summary>
    /// Allows the action or throws. A <see cref="PermissionValue.NeedsApproval"/> permission needs
    /// <paramref name="approvalToken"/> from a user whose role has it <see cref="PermissionValue.Allowed"/>;
    /// the token is used up here. Throws <see cref="PermissionDeniedException"/> or
    /// <see cref="ApprovalRequiredException"/>. Pass the returned grant's approver to the audit log.
    /// </summary>
    Task<PermissionGrant> Require(SessionUser user, string permissionKey, string? approvalToken = null, CancellationToken ct = default);
}

/// <summary>The approval token sent with the current request (header <c>X-Approval-Token</c>).</summary>
public interface IApprovalTokenSource
{
    string? ApprovalToken { get; }
}
