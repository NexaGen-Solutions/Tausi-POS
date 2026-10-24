using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.Devices;

namespace Tausi.Application.Terminals;

/// <summary>
/// Pair with an existing terminal record (<c>TerminalId</c>) or a new one (<c>NewTerminalName</c>).
/// Re-pairing a terminal that is paired to another device needs <c>ConfirmReplace</c>.
/// </summary>
public sealed record PairTerminalRequest(string Code, Guid? TerminalId, string? NewTerminalName, bool ConfirmReplace = false);

public sealed record PairingCompleted(Guid TerminalId, string TerminalName);

public static class PairingCodes
{
    public const string CodeNotFound = "pairing_code";
    public const string AlreadyPaired = "already_paired";
    public const string LicenceLimit = "licence_limit";
}

/// <summary>Five wrong codes from one admin within 5 minutes block further tries for 5 minutes.</summary>
public sealed class PairingGuard(TimeProvider clock)
{
    public const int MaxWrong = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Block = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, List<DateTimeOffset>> _wrong = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _blockedUntil = new();

    public void EnsureAllowed(Guid userId)
    {
        if (_blockedUntil.TryGetValue(userId, out var until) && until > clock.GetUtcNow())
            throw new LoginLockedException(until);
    }

    public void Wrong(Guid userId)
    {
        var now = clock.GetUtcNow();
        var list = _wrong.GetOrAdd(userId, _ => []);
        lock (list)
        {
            list.RemoveAll(t => now - t >= Window);
            list.Add(now);
            if (list.Count < MaxWrong) return;
            list.Clear();
        }
        _blockedUntil[userId] = now + Block;
        throw new LoginLockedException(now + Block);
    }
}

/// <summary>
/// An admin enters the 6-digit code shown on a new terminal and picks which terminal it is. The
/// app then collects its device token. Codes expire after 10 minutes and work once. The licence's
/// terminal limit (when there is a licence) counts paired, not revoked terminals.
/// </summary>
public sealed class PairTerminal(
    ITausiDb db, TerminalAdmin admin, PairingGuard guard, SessionKeeper sessions,
    IAuditLog audit, IPosEvents events, ICurrentSession session, TimeProvider clock)
{
    public async Task<PairingCompleted> Execute(PairTerminalRequest request, CancellationToken ct)
    {
        var grant = await admin.Require(ct);
        var userId = session.RequireUser().UserId;
        guard.EnsureAllowed(userId);

        var now = clock.GetUtcNow();
        var code = new string((request.Code ?? "").Where(char.IsAsciiDigit).ToArray());
        var pairing = await db.TerminalPairings
            .FirstOrDefaultAsync(p => p.Code == code && p.ExpiresAt > now && p.TerminalId == null, ct);
        if (pairing is null)
        {
            guard.Wrong(userId);
            throw new DomainException("Code not found or expired.", PairingCodes.CodeNotFound);
        }

        Terminal terminal;
        if (request.TerminalId is { } id)
        {
            terminal = await db.Terminals.FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new NotFoundException("Terminal not found.");
        }
        else
        {
            var name = request.NewTerminalName?.Trim() ?? "";
            if (name.Length is < 2 or > 40) throw new DomainException("Give the new terminal a name of 2 to 40 characters.");
            var upper = name.ToUpperInvariant();
            if (await db.Terminals.AnyAsync(t => t.Name.ToUpper() == upper, ct))
                throw new DomainException($"There is already a terminal called {name}. Choose it from the list instead.");
            // New terminals sell from the Bar.
            var bar = await db.StockLocations.Where(l => l.Name == Domain.Stock.StockLocation.Bar).Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
            terminal = new Terminal { Name = name, SellsFromLocationId = bar };
            db.Terminals.Add(terminal);
        }

        var replacing = terminal.IsPaired;
        if (replacing && !request.ConfirmReplace)
            throw new ConflictException(
                $"{terminal.Name} is paired with another device. Pairing again logs that device out.", PairingCodes.AlreadyPaired);

        var licence = await db.Licences.AsNoTracking().Where(l => l.TerminalLimit > 0).OrderByDescending(l => l.TerminalLimit).FirstOrDefaultAsync(ct);
        if (licence is not null && !replacing)
        {
            var inUse = await db.Terminals.CountAsync(t => t.DeviceTokenHash != null && t.RevokedAt == null && t.Id != terminal.Id, ct);
            if (inUse >= licence.TerminalLimit)
                throw new DomainException($"Your licence allows {licence.TerminalLimit} terminals.", PairingCodes.LicenceLimit);
        }

        if (replacing) await RevokeTerminal.Revoke(db, sessions, terminal, now, ct);

        var deviceToken = SecureTokens.New();
        terminal.DeviceTokenHash = SecureTokens.Hash(deviceToken);
        terminal.RevokedAt = null;
        pairing.TerminalId = terminal.Id;
        pairing.PendingDeviceToken = deviceToken;

        audit.Write(new AuditRecord(
            AuditActions.TerminalPaired, "Terminal", terminal.Id.ToString(),
            OldValue: new { paired = replacing }, NewValue: new { paired = true, terminal.Name, replacedDevice = replacing },
            ApprovedByUserId: grant.ApprovedByUserId));
        await db.SaveChangesAsync(ct);
        if (replacing) await events.Publish(PosEventNames.TerminalRevoked, new { terminalId = terminal.Id }, ct);
        await events.Publish(PosEventNames.TerminalStatus, new { terminalId = terminal.Id }, ct);
        return new PairingCompleted(terminal.Id, terminal.Name);
    }
}
