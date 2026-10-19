using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Common;
using Nexagen.Application.Security;

namespace Nexagen.Application.Terminals;

public sealed record ClaimPairingRequest(Guid PairingId, string Secret);

/// <summary><c>Status</c> is "waiting" until an admin enters the code, then "paired" once, with the token.</summary>
public sealed record PairingClaim(string Status, string? DeviceToken, Guid? TerminalId, string? TerminalName);

/// <summary>The terminal app polls this until an admin has entered its code, then stores the token.</summary>
public sealed class ClaimPairing(INexagenDb db, TimeProvider clock)
{
    public async Task<PairingClaim> Execute(ClaimPairingRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pairing = await db.TerminalPairings.FirstOrDefaultAsync(p => p.Id == request.PairingId, ct);
        if (pairing is null || pairing.SecretHash != SecureTokens.Hash(request.Secret ?? "") || pairing.ClaimedAt is not null)
            throw new NotFoundException("Pairing not found. Start again.");
        if (pairing.TerminalId is null)
        {
            if (pairing.ExpiresAt <= now) throw new NotFoundException("The pairing code has expired. Start again.");
            return new PairingClaim("waiting", null, null, null);
        }

        var terminal = await db.Terminals.AsNoTracking().FirstAsync(t => t.Id == pairing.TerminalId, ct);
        var token = pairing.PendingDeviceToken;
        pairing.PendingDeviceToken = null;
        pairing.ClaimedAt = now;
        await db.SaveChangesAsync(ct);
        return new PairingClaim("paired", token, terminal.Id, terminal.Name);
    }
}
