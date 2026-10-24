using Tausi.Domain.Common;

namespace Tausi.Domain.People;

public static class SessionEndReasons
{
    public const string Logout = "logout";
    public const string Idle = "idle";
    public const string Expired = "expired";
    public const string Replaced = "replaced";
    public const string PinReset = "pin_reset";
    public const string Deactivated = "deactivated";
    public const string Revoked = "revoked";
    public const string DayClosed = "day_closed";
}

/// <summary>
/// A PIN-login session on a terminal. It ends after a period without use (the business's idle
/// setting), after <see cref="MaxLifetime"/> at the latest, on log out, or when someone else logs
/// in on the same terminal.
/// </summary>
public class UserSession : Entity
{
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(12);

    public Guid UserId { get; set; }
    public Guid TerminalId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }

    /// <summary>Why the session can no longer be used at <paramref name="now"/>, or null if it can.</summary>
    public string? EndReasonAt(DateTimeOffset now, TimeSpan idleLimit)
    {
        if (EndedAt is not null) return EndReason ?? SessionEndReasons.Logout;
        if (now >= ExpiresAt) return SessionEndReasons.Expired;
        if (now - LastSeenAt >= idleLimit) return SessionEndReasons.Idle;
        return null;
    }
}
