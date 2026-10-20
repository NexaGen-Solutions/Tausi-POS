using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Security;
using Tausi.Domain.People;

namespace Tausi.Application.Terminals;

public sealed record TerminalRow(Guid Id, string Name, bool Paired, Guid? PriceListId, DateTimeOffset? LastSeenAt);

/// <summary>Terminals with their price list (devices.manage). Read-only until the Terminals screen.</summary>
public sealed class ListTerminals(ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval)
{
    public async Task<IReadOnlyList<TerminalRow>> Execute(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.DevicesManage, approval.ApprovalToken, ct);
        return await db.Terminals.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new TerminalRow(t.Id, t.Name, t.DeviceTokenHash != null, t.PriceListId, t.LastSeenAt))
            .ToListAsync(ct);
    }
}
