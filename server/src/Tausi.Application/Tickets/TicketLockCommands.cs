using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

/// <summary>Whether this terminal may edit the ticket, and if not, who has it open.</summary>
public sealed record TicketLockState(bool Editable, LockDto? HeldBy, string? Message);

/// <summary>
/// Takes or renews the edit lock on a ticket for this terminal and user. A ticket open on another
/// terminal comes back read-only. The lock fields change with a conditional update, so two
/// terminals can't both win, and a renewal doesn't count as an edit (the row version stays).
/// </summary>
public sealed class LockTicket(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, TicketLocks locks, TicketEvents events, TimeProvider clock)
{
    public async Task<TicketLockState> Execute(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (user.TerminalId is not { } terminalId || user.SessionId is not { } sessionId)
            throw new ForbiddenException("Tickets are opened on a terminal.", "terminal_required");
        await permissions.Require(user, PermissionKeys.OrderAdd, ct: ct);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct)
                ?? throw new NotFoundException("Ticket not found.");
            if (!ticket.IsOpen) return new TicketLockState(false, null, $"#{ticket.Number} is no longer open.");

            var holder = await locks.Holder(ticket, ct);
            if (holder is not null && holder.TerminalId != terminalId)
                return new TicketLockState(false, ToDto(holder), holder.ReadOnlyMessage);

            var now = clock.GetUtcNow();
            var expires = now + Ticket.LockLifetime;
            var seenSession = ticket.LockSessionId;
            var taken = await db.Tickets
                .Where(k => k.Id == id && k.LockSessionId == seenSession)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(k => k.LockTerminalId, terminalId)
                    .SetProperty(k => k.LockUserId, user.UserId)
                    .SetProperty(k => k.LockSessionId, sessionId)
                    .SetProperty(k => k.LockExpiresAt, expires), ct);
            if (taken != 1) continue; // someone else took it in between: look again

            var mine = new LockHolder(terminalId, user.TerminalName ?? "", user.UserId, user.Name);
            if (holder is null || holder.UserId != user.UserId)
                await events.Lock(id, ticket.TableId, mine, ct);
            return new TicketLockState(true, ToDto(mine), null);
        }
        throw new ConflictException("Someone else is opening this ticket. Try again.");
    }

    internal static LockDto ToDto(LockHolder h) => new(h.TerminalId, h.TerminalName, h.UserId, h.UserName);
}

/// <summary>The user leaves the ticket: this terminal's lock on it ends.</summary>
public sealed class UnlockTicket(ITausiDb db, ICurrentSession session, TicketEvents events)
{
    public async Task Execute(Guid id, CancellationToken ct)
    {
        var user = session.RequireUser();
        if (user.TerminalId is not { } terminalId) return;
        var ticket = await db.Tickets.AsNoTracking().Where(k => k.Id == id)
            .Select(k => new { k.TableId, k.LockTerminalId }).FirstOrDefaultAsync(ct);
        if (ticket is null || ticket.LockTerminalId != terminalId) return;

        var released = await db.Tickets.Where(k => k.Id == id && k.LockTerminalId == terminalId)
            .ExecuteUpdateAsync(Clear, ct);
        if (released == 1) await events.Lock(id, ticket.TableId, null, ct);
    }

    internal static void Clear(UpdateSettersBuilder<Ticket> u) => u
        .SetProperty(k => k.LockTerminalId, (Guid?)null)
        .SetProperty(k => k.LockUserId, (Guid?)null)
        .SetProperty(k => k.LockSessionId, (Guid?)null)
        .SetProperty(k => k.LockExpiresAt, (DateTimeOffset?)null);
}

/// <summary>
/// Ends locks that no longer have a terminal behind them: not renewed for 90 seconds, or whose
/// session ended (logout, idle logout, PIN reset, deactivation, revoked terminal). Run every few
/// seconds in the background, and straight away for a session that logs out.
/// </summary>
public sealed class ReleaseTicketLocks(ITausiDb db, TicketEvents events, TimeProvider clock)
{
    public async Task<int> Stale(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var held = await db.Tickets.AsNoTracking().Where(k => k.LockSessionId != null)
            .Select(k => new { k.Id, k.TableId, k.LockSessionId, k.LockExpiresAt }).ToListAsync(ct);
        if (held.Count == 0) return 0;

        var sessionIds = held.Select(h => h.LockSessionId!.Value).Distinct().ToList();
        var sessions = await db.UserSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var idleLimit = (await PosSettings.Load(db, ct)).IdleLimit;

        var stale = held.Where(h =>
            h.LockExpiresAt is not { } expires || expires <= now
            || !sessions.TryGetValue(h.LockSessionId!.Value, out var s) || s.EndReasonAt(now, idleLimit) is not null).ToList();
        var released = 0;
        foreach (var lockRow in stale)
        {
            var sessionId = lockRow.LockSessionId;
            if (await db.Tickets.Where(k => k.Id == lockRow.Id && k.LockSessionId == sessionId).ExecuteUpdateAsync(UnlockTicket.Clear, ct) == 1)
            {
                released++;
                await events.Lock(lockRow.Id, lockRow.TableId, null, ct);
            }
        }
        return released;
    }

    public async Task ForSession(Guid sessionId, CancellationToken ct)
    {
        var held = await db.Tickets.AsNoTracking().Where(k => k.LockSessionId == sessionId)
            .Select(k => new { k.Id, k.TableId }).ToListAsync(ct);
        foreach (var lockRow in held)
        {
            if (await db.Tickets.Where(k => k.Id == lockRow.Id && k.LockSessionId == sessionId).ExecuteUpdateAsync(UnlockTicket.Clear, ct) == 1)
                await events.Lock(lockRow.Id, lockRow.TableId, null, ct);
        }
    }
}
