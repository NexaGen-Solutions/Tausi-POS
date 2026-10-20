using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;

namespace Tausi.Application.Auth;

/// <summary>
/// The rules every new PIN follows, whoever sets it: 4-6 digits, not easy to guess, and not the
/// PIN of another active user (refused without saying whose).
/// </summary>
public sealed class PinRules(ITausiDb db, IPinHasher hasher)
{
    public async Task Check(string pin, Guid? forUserId, CancellationToken ct)
    {
        if (!Pin.IsWellFormed(pin))
            throw new DomainException($"A PIN must be {Pin.MinLength} to {Pin.MaxLength} digits.", PinChangeCodes.Invalid);
        if (Pin.IsTooSimple(pin))
            throw new DomainException("That PIN is too easy to guess. Choose another.", PinChangeCodes.TooSimple);
        var lookup = hasher.Lookup(pin);
        if (await db.Users.AnyAsync(u => u.Active && u.Id != forUserId && u.PinLookup == lookup, ct))
            throw Taken();
    }

    /// <summary>A random PIN that passes <see cref="Check"/>.</summary>
    public async Task<string> Generate(Guid? forUserId, CancellationToken ct, int length = 4)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var pin = SecureTokens.Digits(length);
            if (Pin.IsTooSimple(pin)) continue;
            var lookup = hasher.Lookup(pin);
            if (!await db.Users.AnyAsync(u => u.Active && u.Id != forUserId && u.PinLookup == lookup, ct))
                return pin;
        }
        return await Generate(forUserId, ct, Math.Min(length + 1, Pin.MaxLength));
    }

    /// <summary>Stores the PIN (hashed) on the user.</summary>
    public void Apply(User user, string pin)
    {
        user.PinHash = hasher.Hash(pin);
        user.PinLookup = hasher.Lookup(pin);
        user.PinLength = pin.Length;
    }

    public static DomainException Taken() => new("Choose a different PIN.", PinChangeCodes.Taken);
}
