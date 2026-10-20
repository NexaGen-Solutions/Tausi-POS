using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;

namespace Tausi.Application.Auth;

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
public sealed class ChangePin(ITausiDb db, ICurrentSession session, IPinHasher hasher, PinRules rules, IAuditLog audit)
{
    public async Task Execute(ChangePinRequest request, CancellationToken ct)
    {
        var current = session.RequireUser();
        var pin = request.NewPin ?? "";
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == current.UserId, ct)
            ?? throw new NotFoundException("User not found.");
        if (Pin.IsWellFormed(pin) && hasher.Verify(pin, user.PinHash))
            throw new DomainException("Choose a PIN different from your current one.", PinChangeCodes.Unchanged);
        await rules.Check(pin, user.Id, ct);

        rules.Apply(user, pin);
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
            throw PinRules.Taken();
        }
    }
}
