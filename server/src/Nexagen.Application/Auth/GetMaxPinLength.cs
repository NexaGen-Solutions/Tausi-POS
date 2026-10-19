using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Security;
using Nexagen.Domain.People;

namespace Nexagen.Application.Auth;

/// <summary>
/// The longest PIN any active user has, so the pad can submit by itself at that length.
/// Only a number: nothing about which PINs exist.
/// </summary>
public sealed class GetMaxPinLength(INexagenDb db, ICurrentSession session)
{
    public async Task<int> Execute(CancellationToken ct)
    {
        session.RequireTerminal();
        var longest = await db.Users.Where(u => u.Active).MaxAsync(u => (int?)u.PinLength, ct);
        return Math.Clamp(longest ?? Pin.MaxLength, Pin.MinLength, Pin.MaxLength);
    }
}
