using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Settings;
using Nexagen.Domain.People;

namespace Nexagen.Application.Auth;

/// <summary>
/// Server-side PIN sessions. A session is usable until it has gone unused for the business's
/// idle time (<c>pos.idleLogoutMinutes</c>), reaches its 12-hour maximum, or is ended.
/// Every authenticated request refreshes it.
/// </summary>
public sealed class SessionKeeper(INexagenDb db, TimeProvider clock)
{
    public async Task<UserSession> Start(Guid userId, Guid terminalId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // One person at a time per terminal: a new login ends whoever was logged in there.
        await db.UserSessions
            .Where(s => s.TerminalId == terminalId && s.EndedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.EndedAt, now)
                .SetProperty(s => s.EndReason, SessionEndReasons.Replaced), ct);

        var session = new UserSession
        {
            UserId = userId,
            TerminalId = terminalId,
            StartedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + UserSession.MaxLifetime,
        };
        db.UserSessions.Add(session);
        return session;
    }

    /// <summary>
    /// Checks the session for a request. Returns null when it is usable (and, if
    /// <paramref name="refresh"/>, marks it as used now), else the reason it ended.
    /// </summary>
    public async Task<string?> Check(Guid sessionId, Guid userId, Guid terminalId, bool refresh, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var session = await db.UserSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.UserId != userId || session.TerminalId != terminalId)
            return SessionEndReasons.Expired;

        var settings = await PosSettings.Load(db, ct);
        var ended = session.EndReasonAt(now, settings.IdleLimit);
        if (ended is not null)
        {
            if (session.EndedAt is null) await End(sessionId, ended, ct);
            return ended;
        }

        if (refresh)
            await db.UserSessions.Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenAt, now), ct);
        return null;
    }

    public async Task End(Guid sessionId, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.UserSessions
            .Where(s => s.Id == sessionId && s.EndedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.EndedAt, now)
                .SetProperty(s => s.EndReason, reason), ct);
    }

    /// <summary>Ends every open session of a user, on every terminal.</summary>
    public async Task EndAllFor(Guid userId, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.UserSessions
            .Where(s => s.UserId == userId && s.EndedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.EndedAt, now)
                .SetProperty(s => s.EndReason, reason), ct);
    }
}
