using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;

namespace Tausi.Application.Terminals;

public sealed record TerminalPriceListRequest(Guid? PriceListId);

/// <summary>
/// Makes a terminal sell from a price list other than the default (null: back to the default).
/// Needs devices.manage. The Terminals screen will use this; for now it is API only.
/// </summary>
public sealed class SetTerminalPriceList(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    IAuditLog audit, IPosEvents events)
{
    public async Task Execute(Guid terminalId, TerminalPriceListRequest request, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.DevicesManage, approval.ApprovalToken, ct);
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == terminalId, ct)
            ?? throw new NotFoundException("Terminal not found.");
        if (request.PriceListId is { } id && !await db.PriceLists.AnyAsync(l => l.Id == id && l.Active, ct))
            throw new DomainException("Choose an active price list.");

        var old = terminal.PriceListId;
        terminal.PriceListId = request.PriceListId;
        audit.Write(new AuditRecord(AuditActions.TerminalPriceList, "Terminal", terminal.Id.ToString(),
            new { priceListId = old }, new { priceListId = request.PriceListId }, grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.MenuChanged, new { }, ct);
    }
}
