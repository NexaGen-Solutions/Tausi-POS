using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Auditing;
using Nexagen.Application.Common;
using Nexagen.Application.Events;
using Nexagen.Application.Security;
using Nexagen.Domain.Common;
using Nexagen.Domain.People;

namespace Nexagen.Application.Terminals;

public sealed record TerminalPriceListRequest(Guid? PriceListId);

/// <summary>
/// Makes a terminal sell from a price list other than the default (null: back to the default).
/// Needs devices.manage. The Terminals screen will use this; for now it is API only.
/// </summary>
public sealed class SetTerminalPriceList(
    INexagenDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
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
