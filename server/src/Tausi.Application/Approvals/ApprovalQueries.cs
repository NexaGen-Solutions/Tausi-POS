using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Tickets;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Approvals;

/// <param name="Where">"Table 7", "Pool 1" or "Takeaway · Otieno".</param>
/// <param name="SentToMe">The requester chose me ("Sent to you").</param>
/// <param name="CanDecide">I may approve or reject it (Allowed for the action, not my own, still pending).</param>
public sealed record ApprovalRequestDto(
    Guid Id, string Kind, string Status, Guid TicketId, long TicketNumber, string Where, Guid? LineId,
    string Summary, decimal? Amount, string? Reason,
    Guid RequestedById, string RequestedBy, string Terminal, DateTimeOffset RequestedAt,
    Guid? PreferredApproverId, bool SentToMe, bool Mine, bool CanDecide,
    string? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionNote, bool? WasPoured = null);

/// <summary>Someone who can approve a kind of request now, for "Send request to".</summary>
public sealed record ApproverDto(Guid UserId, string Name, string Role, string Terminal);

internal static class ApprovalList
{
    public static async Task<List<ApprovalRequestDto>> Load(
        ITausiDb db, IReadOnlyCollection<Guid> ids, SessionUser me, IPermissionService permissions, CancellationToken ct)
    {
        var rows = await db.ApprovalRequests.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        return await ToDtos(db, rows, me, permissions, ct);
    }

    public static async Task<List<ApprovalRequestDto>> ToDtos(
        ITausiDb db, List<ApprovalRequest> rows, SessionUser me, IPermissionService permissions, CancellationToken ct)
    {
        var userIds = rows.Select(r => r.RequestedByUserId).Concat(rows.Where(r => r.DecidedByUserId != null).Select(r => r.DecidedByUserId!.Value)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var terminalIds = rows.Select(r => r.TerminalId).Distinct().ToList();
        var terminals = await db.Terminals.AsNoTracking().Where(t => terminalIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var ticketIds = rows.Select(r => r.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking().Include(k => k.Table).Where(k => ticketIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, ct);
        var allowed = new Dictionary<ApprovalKind, bool>();
        foreach (var kind in rows.Select(r => r.Kind).Distinct())
            allowed[kind] = await permissions.Check(me, ApprovalKinds.Permission(kind), ct) == PermissionValue.Allowed;

        return rows.Select(r =>
        {
            var ticket = tickets[r.TicketId];
            var mine = r.RequestedByUserId == me.UserId;
            return new ApprovalRequestDto(
                r.Id, r.Kind.ToString(), r.Status.ToString(), r.TicketId, ticket.Number, TicketPrints.Where(ticket), r.OrderLineId,
                r.Summary, r.Amount, r.Reason, r.RequestedByUserId, users.GetValueOrDefault(r.RequestedByUserId, "?"),
                terminals.GetValueOrDefault(r.TerminalId, "?"), r.RequestedAt, r.PreferredApproverId,
                r.PreferredApproverId == me.UserId, mine, r.IsPending && !mine && allowed[r.Kind],
                r.DecidedByUserId is { } d ? users.GetValueOrDefault(d, "?") : null, r.DecidedAt, r.DecisionNote,
                r.Kind == ApprovalKind.Void ? ApprovalActions.Payload<VoidLineRequest>(r.PayloadJson).WasPoured ?? false : null);
        }).ToList();
    }
}

/// <summary>
/// <c>status=pending</c>: the requests I can decide (the ones sent to me first) and my own.
/// <c>status=decided</c>: "Earlier this shift", the decided ones of the current work period.
/// Requests go to everyone who can approve them, so none gets stuck when one supervisor leaves.
/// </summary>
public sealed class ListApprovals(ITausiDb db, ICurrentSession session, IPermissionService permissions, ExpireApprovals expire)
{
    public async Task<List<ApprovalRequestDto>> Execute(string? status, CancellationToken ct)
    {
        var me = session.RequireUser();
        await expire.Run(ct);
        var decided = string.Equals(status, "decided", StringComparison.OrdinalIgnoreCase);
        if (!decided && !string.IsNullOrEmpty(status) && !string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
            throw new Domain.Common.DomainException("status must be pending or decided.");

        var kinds = new List<ApprovalKind>();
        foreach (var kind in Enum.GetValues<ApprovalKind>())
            if (await permissions.Check(me, ApprovalKinds.Permission(kind), ct) == PermissionValue.Allowed) kinds.Add(kind);

        var query = db.ApprovalRequests.AsNoTracking().Where(r => kinds.Contains(r.Kind) || r.RequestedByUserId == me.UserId);
        if (decided)
        {
            var day = await WorkPeriods.Current(db, ct);
            if (day is null) return [];
            query = query.Where(r => r.Status != ApprovalStatus.Pending && r.WorkPeriodId == day.Id);
        }
        else query = query.Where(r => r.Status == ApprovalStatus.Pending);

        var rows = await query.ToListAsync(ct);
        var list = await ApprovalList.ToDtos(db, rows, me, permissions, ct);
        return decided
            ? list.OrderByDescending(r => r.DecidedAt).Take(100).ToList()
            : list.OrderByDescending(r => r.SentToMe).ThenBy(r => r.Mine).ThenBy(r => r.RequestedAt).ToList();
    }
}

/// <summary>The people logged in now who could approve a kind of request, apart from me.</summary>
public sealed class Approvers(ITausiDb db, ICurrentSession session, IPermissionService permissions, TimeProvider clock)
{
    public async Task<List<ApproverDto>> Execute(ApprovalKind kind, CancellationToken ct)
    {
        var me = session.RequireUser();
        return await LoggedIn(kind, me.UserId, ct);
    }

    public async Task<bool> CanDecide(Guid userId, ApprovalKind kind, Guid requesterId, CancellationToken ct) =>
        (await LoggedIn(kind, requesterId, ct)).Any(a => a.UserId == userId);

    private async Task<List<ApproverDto>> LoggedIn(ApprovalKind kind, Guid except, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var idle = (await PosSettings.Load(db, ct)).IdleLimit;
        var sessions = await db.UserSessions.AsNoTracking()
            .Where(s => s.EndedAt == null && s.ExpiresAt > now && s.UserId != except)
            .ToListAsync(ct);
        sessions = sessions.Where(s => s.EndReasonAt(now, idle) is null).ToList();
        var userIds = sessions.Select(s => s.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Include(u => u.Role).Where(u => userIds.Contains(u.Id) && u.Active).ToListAsync(ct);
        var terminalIds = sessions.Select(s => s.TerminalId).Distinct().ToList();
        var terminals = await db.Terminals.AsNoTracking().Where(t => terminalIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var result = new List<ApproverDto>();
        foreach (var user in users.OrderBy(u => u.Name))
        {
            var asUser = new SessionUser(user.Id, user.Name, user.RoleId, user.Role!.Name, null, null);
            if (await permissions.Check(asUser, ApprovalKinds.Permission(kind), ct) != PermissionValue.Allowed) continue;
            var where = sessions.Where(s => s.UserId == user.Id).OrderByDescending(s => s.LastSeenAt).First();
            result.Add(new ApproverDto(user.Id, user.Name, user.Role.Name, terminals.GetValueOrDefault(where.TerminalId, "?")));
        }
        return result;
    }
}
