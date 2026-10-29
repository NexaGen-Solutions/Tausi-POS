using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

/// <summary>
/// Whose tickets a user may work on. A ticket belongs to the waiter who opened it; others need
/// <c>ticket.others</c> (Allowed: cashiers, supervisors and admins by default) to change it or to
/// start another ticket on its table. Without it, others' tickets open read-only; and with
/// <c>pos.showOthersTickets</c> off they show on the map only as taken, and don't open at all.
/// </summary>
public sealed class TicketAccess(ITausiDb db, IPermissionService permissions)
{
    public async Task<bool> AnyTicket(SessionUser user, CancellationToken ct) =>
        await permissions.Check(user, PermissionKeys.TicketOthers, ct) == PermissionValue.Allowed;

    /// <summary>Others' tickets are hidden from this user (only "taken" on the map).</summary>
    public async Task<bool> OthersHidden(SessionUser user, CancellationToken ct) =>
        !(await PosSettings.Load(db, ct)).ShowOthersTickets && !await AnyTicket(user, ct);

    public async Task<bool> MayChange(SessionUser user, Ticket ticket, CancellationToken ct) =>
        ticket.OpenedByUserId == user.UserId || await AnyTicket(user, ct);

    public async Task EnsureMayChange(SessionUser user, Ticket ticket, CancellationToken ct)
    {
        if (await MayChange(user, ticket, ct)) return;
        throw new ForbiddenException(
            $"#{ticket.Number} is {await OwnerName(ticket, ct)}'s ticket. You can view it but not change it.", TicketCodes.NotYours);
    }

    /// <summary>A waiter starts a ticket on a table only if no other waiter has an open ticket there.</summary>
    public async Task EnsureTableFree(SessionUser user, Guid tableId, string tableName, Guid? exceptTicketId, CancellationToken ct)
    {
        var owner = await db.Tickets.AsNoTracking()
            .Where(k => k.TableId == tableId && k.Status == TicketStatus.Open && k.OpenedByUserId != user.UserId && k.Id != exceptTicketId)
            .Select(k => db.Users.Where(u => u.Id == k.OpenedByUserId).Select(u => u.Name).FirstOrDefault())
            .FirstOrDefaultAsync(ct);
        if (owner is null || await AnyTicket(user, ct)) return;
        throw new ForbiddenException($"{tableName} is {owner}'s table. Ask {owner} or a supervisor.", TicketCodes.TableTaken);
    }

    public async Task<string> OwnerName(Ticket ticket, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == ticket.OpenedByUserId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "another waiter";
}
