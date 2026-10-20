using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Security;
using Tausi.Domain.Devices;

namespace Tausi.Application.Terminals;

public sealed record PairingStarted(Guid PairingId, string Code, string Secret, DateTimeOffset ExpiresAt);

/// <summary>A new terminal app asks for a pairing code to show on its screen. No login needed.</summary>
public sealed class StartPairing(ITausiDb db, TimeProvider clock)
{
    public async Task<PairingStarted> Execute(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.TerminalPairings.Where(p => p.ExpiresAt < now).ExecuteDeleteAsync(ct);

        string code;
        do code = SecureTokens.Digits(6);
        while (await db.TerminalPairings.AnyAsync(p => p.Code == code, ct));

        var secret = SecureTokens.New();
        var pairing = new TerminalPairing
        {
            Code = code,
            SecretHash = SecureTokens.Hash(secret),
            CreatedAt = now,
            ExpiresAt = now + TerminalPairing.Lifetime,
        };
        db.TerminalPairings.Add(pairing);
        await db.SaveChangesAsync(ct);
        return new PairingStarted(pairing.Id, code, secret, pairing.ExpiresAt);
    }
}
