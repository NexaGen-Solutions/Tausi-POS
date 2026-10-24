using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.People;

namespace Tausi.Application.Stock;

public static class StockCodes
{
    public const string OwnDocument = "own_document";
    public const string NotPending = "not_pending";
    public const string InvoiceUsed = "invoice_used";
    public const string AlreadySold = "already_sold";
    public const string Uncounted = "uncounted";
    public const string BaseUnitLocked = "base_unit_locked";
}

/// <summary>
/// Who may do what with stock on the admin site. There is no approval pad there, so a stock key
/// set to "Needs approval" means the document is saved as Pending, and someone Allowed for that key
/// (not its creator) approves it before anything moves.
/// </summary>
public sealed class StockAccess(IPermissionService permissions)
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
            throw new ForbiddenException("You can't approve your own. Ask someone else who is allowed.", StockCodes.OwnDocument);
    }

    public Task<PermissionValue> Check(SessionUser user, string key, CancellationToken ct) => permissions.Check(user, key, ct);
}

public static class StockEvents
{
    public const string Changed = "stock.changed";

    public static Task Publish(IPosEvents events, string what, CancellationToken ct) => events.Publish(Changed, new { what }, ct);
}
