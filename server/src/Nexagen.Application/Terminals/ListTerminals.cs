using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Security;
using Nexagen.Domain.People;

namespace Nexagen.Application.Terminals;

public sealed record TerminalRow(Guid Id, string Name, bool Paired, Guid? PriceListId, DateTimeOffset? LastSeenAt);

/// <summary>Terminals with their price list (devices.manage). Read-only until the Terminals screen.</summary>
public sealed class ListTerminals(INexagenDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval)
{
    public async Task<IReadOnlyList<TerminalRow>> Execute(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.DevicesManage, approval.ApprovalToken, ct);
        return await db.Terminals.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new TerminalRow(t.Id, t.Name, t.DeviceTokenHash != null, t.PriceListId, t.LastSeenAt))
            .ToListAsync(ct);
    }
}
