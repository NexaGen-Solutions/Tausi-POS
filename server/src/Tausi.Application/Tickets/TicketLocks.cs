using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Settings;
using Tausi.Domain.Service;

namespace Tausi.Application.Tickets;

/// <summary>Who has a ticket open for editing.</summary>
public sealed record LockHolder(Guid TerminalId, string TerminalName, Guid UserId, string UserName)
{
    /// <summary>"Open on Pillar 1 by Ruth. You can view it but not change it."</summary>
    public string ReadOnlyMessage => $"Open on {TerminalName} by {UserName}. You can view it but not change it.";
}

/// <summary>
/// Edit locks: a ticket on screen on one terminal can only be changed there. A lock lives while
/// its terminal renews it (every 30 seconds, gone after 90) and while its session lasts.
/// </summary>
public sealed class TicketLocks(ITausiDb db, TimeProvider clock)
{
    /// <summary>The live holder of the ticket's lock, or null when nobody holds it.</summary>
    public async Task<LockHolder?> Holder(Ticket ticket, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (ticket.LockAt(now) is not { } held) return null;

        var session = await db.UserSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == held.SessionId, ct);
        var settings = await PosSettings.Load(db, ct);
        if (session is null || session.EndReasonAt(now, settings.IdleLimit) is not null) return null;

        var terminal = await db.Terminals.AsNoTracking().Where(t => t.Id == held.TerminalId).Select(t => t.Name).FirstOrDefaultAsync(ct);
        var user = await db.Users.AsNoTracking().Where(u => u.Id == held.UserId).Select(u => u.Name).FirstOrDefaultAsync(ct);
        return new LockHolder(held.TerminalId, terminal ?? "another terminal", held.UserId, user ?? "someone");
    }

    /// <summary>
    /// Refuses when any of these tickets is open on another terminal. A lock held by this terminal
    /// is fine (move and merge happen on the terminal that has the ticket open).
    /// </summary>
    public async Task EnsureFreeFor(IEnumerable<Ticket> tickets, Guid? terminalId, CancellationToken ct)
    {
        foreach (var ticket in tickets)
        {
            if (await Holder(ticket, ct) is { } holder && holder.TerminalId != terminalId)
                throw new ConflictException(
                    $"#{ticket.Number} is open on {holder.TerminalName} by {holder.UserName}. Wait until they leave it, then try again.",
                    TicketCodes.Locked);
        }
    }
}

public static class TicketCodes
{
    public const string Locked = "ticket_locked";
    public const string DayClosed = "day_closed";
    public const string TableInUse = "table_in_use";
}
