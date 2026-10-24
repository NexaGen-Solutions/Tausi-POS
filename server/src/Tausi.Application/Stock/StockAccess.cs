using Tausi.Application.Approvals;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.People;

namespace Tausi.Application.Stock;

public static class StockCodes
{
    public const string OwnDocument = AdminApprovals.OwnDocument;
    public const string NotPending = AdminApprovals.NotPending;
    public const string InvoiceUsed = "invoice_used";
    public const string AlreadySold = "already_sold";
    public const string Uncounted = "uncounted";
    public const string BaseUnitLocked = "base_unit_locked";
}

/// <summary>
/// Who may see and do what with stock on the admin site. Pending documents and their approval go
/// through the shared <see cref="AdminApprovals"/>.
/// </summary>
public sealed class StockAccess(IPermissionService permissions, AdminApprovals approvals)
{
    public static readonly string[] Keys =
        [PermissionKeys.StockReceive, PermissionKeys.StockWaste, PermissionKeys.StockCount, PermissionKeys.StockAdjust];

    /// <summary>Seeing the stock pages needs any stock key that isn't Not allowed.</summary>
    public async Task RequireAny(SessionUser user, CancellationToken ct)
    {
        foreach (var key in Keys)
            if (await permissions.Check(user, key, ct) != PermissionValue.NotAllowed) return;
        throw new PermissionDeniedException(PermissionKeys.StockReceive);
    }

    public Task<bool> PostsNow(SessionUser user, string key, CancellationToken ct) => approvals.PostsNow(user, key, ct);

    public Task RequireApprover(SessionUser user, string key, Guid creatorId, CancellationToken ct) =>
        approvals.RequireApprover(user, key, creatorId, ct);

    public Task<PermissionValue> Check(SessionUser user, string key, CancellationToken ct) => permissions.Check(user, key, ct);
}

public static class StockEvents
{
    public const string Changed = "stock.changed";

    public static Task Publish(IPosEvents events, string what, CancellationToken ct) => events.Publish(Changed, new { what }, ct);
}
