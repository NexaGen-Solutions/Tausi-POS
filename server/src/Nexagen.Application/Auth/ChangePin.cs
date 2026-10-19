using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Auditing;
using Nexagen.Application.Common;
using Nexagen.Application.Security;
using Nexagen.Domain.Common;
using Nexagen.Domain.People;

namespace Nexagen.Application.Auth;

public sealed record ChangePinRequest(string NewPin);

public static class PinChangeCodes
{
    public const string Invalid = "pin_invalid";
    public const string TooSimple = "pin_too_simple";
    public const string Unchanged = "pin_unchanged";
    public const string Taken = "pin_taken";
}

/// <summary>
/// A logged-in user chooses a new PIN (on first login, or after an admin reset). The PIN may not
/// be easy to guess, the same as before, or used by another active user; the last case does not
/// say who has it.
/// </summary>
public sealed class ChangePin(INexagenDb db, ICurrentSession session, IPinHasher hasher, IAuditLog audit)
{
    public async Task Execute(ChangePinRequest request, CancellationToken ct)
    {
        var current = session.RequireUser();
        var pin = request.NewPin ?? "";
        if (!Pin.IsWellFormed(pin))
            throw new DomainException($"A PIN must be {Pin.MinLength} to {Pin.MaxLength} digits.", PinChangeCodes.Invalid);
        if (Pin.IsTooSimple(pin))
            throw new DomainException("That PIN is too easy to guess. Choose another.", PinChangeCodes.TooSimple);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == current.UserId, ct)
            ?? throw new NotFoundException("User not found.");
        if (hasher.Verify(pin, user.PinHash))
            throw new DomainException("Choose a PIN different from your current one.", PinChangeCodes.Unchanged);

        var lookup = hasher.Lookup(pin);
        if (await db.Users.AnyAsync(u => u.Active && u.Id != user.Id && u.PinLookup == lookup, ct))
            throw Taken();

        user.PinHash = hasher.Hash(pin);
        user.PinLookup = lookup;
        user.PinLength = pin.Length;
        user.MustChangePin = false;
        audit.Write(new AuditRecord(
            AuditActions.PinChanged, "User", user.Id.ToString(),
            OldValue: new { mustChangePin = true }, NewValue: new { mustChangePin = false }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Someone else took the same PIN at the same moment: the unique index caught it.
            throw Taken();
        }
    }

    private static DomainException Taken() => new("Choose a different PIN.", PinChangeCodes.Taken);
}
