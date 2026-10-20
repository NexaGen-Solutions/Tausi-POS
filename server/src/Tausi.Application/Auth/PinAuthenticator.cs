using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.People;

namespace Tausi.Application.Auth;

/// <summary>
/// Finds the user a PIN belongs to, on a given terminal, applying the wrong-PIN lockout:
/// 5 wrong PINs within 2 minutes lock the terminal for 60 seconds.
/// </summary>
public sealed class PinAuthenticator(ITausiDb db, IPinHasher hasher, IAuditLog audit, TimeProvider clock)
{
    public async Task<User> Authenticate(Guid terminalId, string pin, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == terminalId, ct)
            ?? throw new UnauthenticatedException("This device is not a registered terminal.");
        if (terminal.PinLockedUntil > now)
            throw new LoginLockedException(terminal.PinLockedUntil.Value);
        if (!terminal.Active)
            throw new LoginRejectedException("This terminal is switched off in the admin site.");

        var user = await FindUser(pin, ct);
        if (user is not null)
        {
            await db.PinFailures.Where(f => f.TerminalId == terminalId).ExecuteDeleteAsync(ct);
            return user;
        }

        db.PinFailures.Add(new PinFailure { TerminalId = terminalId, At = now });
        audit.Write(new AuditRecord(AuditActions.PinLoginFailed, "Terminal", terminalId.ToString(), TerminalId: terminalId));

        var since = now - LoginLockout.Window;
        var recent = await db.PinFailures.AsNoTracking()
            .Where(f => f.TerminalId == terminalId && f.At > since)
            .Select(f => f.At)
            .ToListAsync(ct);
        recent.Add(now);

        if (LoginLockout.ShouldLock(recent, now))
        {
            terminal.PinLockedUntil = now + LoginLockout.LockFor;
            audit.Write(new AuditRecord(
                AuditActions.TerminalLocked, "Terminal", terminalId.ToString(),
                NewValue: new { lockedUntil = terminal.PinLockedUntil, failures = recent.Count },
                TerminalId: terminalId));
            await db.SaveChangesAsync(ct);
            await db.PinFailures.Where(f => f.TerminalId == terminalId).ExecuteDeleteAsync(ct);
            throw new LoginLockedException(terminal.PinLockedUntil.Value);
        }

        await db.SaveChangesAsync(ct);
        throw new LoginRejectedException("That PIN is not right.", LoginLockout.MaxFailures - recent.Count);
    }

    private async Task<User?> FindUser(string pin, CancellationToken ct)
    {
        if (pin.Length is < Pin.MinLength or > Pin.MaxLength || !pin.All(char.IsAsciiDigit))
            return null;
        var lookup = hasher.Lookup(pin);
        var user = await db.Users.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Active && u.PinLookup == lookup, ct);
        return user is not null && hasher.Verify(pin, user.PinHash) ? user : null;
    }
}
