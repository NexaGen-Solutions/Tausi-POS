using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.People;

namespace Tausi.Application.Approvals;

/// <summary>
/// Approvals on the admin site, shared by stock and money documents. There is no approval pad there,
/// so a key set to "Needs approval" means the document is saved as Pending, and someone Allowed for
/// that key (not its creator) approves it before anything is posted.
/// </summary>
public sealed class AdminApprovals(IPermissionService permissions)
{
    public const string OwnDocument = "own_document";
    public const string NotPending = "not_pending";

    /// <summary>True: post now (Allowed). False: save as Pending (Needs approval). Not allowed throws.</summary>
    public async Task<bool> PostsNow(SessionUser user, string key, CancellationToken ct) =>
        await permissions.Check(user, key, ct) switch
        {
            PermissionValue.Allowed => true,
            PermissionValue.NeedsApproval => false,
            _ => throw new PermissionDeniedException(key),
        };

    /// <summary>The approver must be Allowed for the key, and not the person who made the document.</summary>
    public async Task RequireApprover(SessionUser user, string key, Guid creatorId, CancellationToken ct)
    {
        if (await permissions.Check(user, key, ct) != PermissionValue.Allowed) throw new PermissionDeniedException(key);
        if (user.UserId == creatorId)
            throw new ForbiddenException("You can't approve your own. Ask someone else who is allowed.", OwnDocument);
    }

    public Task<PermissionValue> Check(SessionUser user, string key, CancellationToken ct) => permissions.Check(user, key, ct);
}
