using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Security;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

/// <summary>
/// Void, gift, discount and price change from the order screen, done now: by a user whose role
/// has the action Allowed, or with a supervisor's PIN on the spot (the approval token). Who can't
/// do it now sends a request instead (<see cref="Approvals.CreateApprovalRequest"/>). Each runs with
/// the ticket row locked and this terminal holding the edit lock.
/// </summary>
public sealed class TicketActionCommands(
    ITausiDb db, TicketEditing editing, IPermissionService permissions, IApprovalTokenSource approval,
    TicketActions actions, TicketEvents events)
{
    public Task<TicketDetailDto> Void(Guid ticketId, Guid lineId, VoidLineRequest r, CancellationToken ct) =>
        Run(ticketId, lineId, ApprovalKind.Void, (ticket, actor) => actions.Void(ticket, lineId, r, actor, ct), ct);

    public Task<TicketDetailDto> Gift(Guid ticketId, Guid lineId, GiftLineRequest r, CancellationToken ct) =>
        Run(ticketId, lineId, ApprovalKind.Gift, (ticket, actor) => actions.Gift(ticket, lineId, r, actor, ct), ct);

    public Task<TicketDetailDto> ChangePrice(Guid ticketId, Guid lineId, ChangePriceRequest r, CancellationToken ct) =>
        Run(ticketId, lineId, ApprovalKind.PriceChange, (ticket, actor) => actions.ChangePrice(ticket, lineId, r, actor, ct), ct);

    public Task<TicketDetailDto> Discount(Guid ticketId, DiscountRequest r, CancellationToken ct) =>
        Run(ticketId, null, ApprovalKind.Discount, (ticket, actor) => actions.Discount(ticket, r, actor, ct), ct);

    private async Task<TicketDetailDto> Run(Guid ticketId, Guid? lineId, ApprovalKind kind, Func<Ticket, TicketActor, Task> act, CancellationToken ct)
    {
        await using var edit = await editing.Open(ticketId, ct);
        await PendingRequests.EnsureNone(db, ticketId, lineId, kind, null, ct);
        var grant = await permissions.Require(edit.User, ApprovalKinds.Permission(kind), approval.ApprovalToken, ct);
        // Allowed users approve their own action; with a PIN, the supervisor did.
        var approverId = grant.ApprovedByUserId ?? edit.User.UserId;
        var approverName = approverId == edit.User.UserId
            ? edit.User.Name
            : await Users.Name(db, approverId, ct);
        await act(edit.Ticket, new TicketActor(edit.User.UserId, edit.User.Name, edit.TerminalId, approverId, approverName));
        await db.SaveChangesAsync(ct);
        await edit.Transaction.CommitAsync(ct);
        await events.Changed(edit.Ticket, edit.Ticket.TableId, ct);
        return await TicketDetails.Mine(db, edit.Ticket, edit.User, ct);
    }
}

public static class Users
{
    public static async Task<string> Name(ITausiDb db, Guid id, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "?";
}
