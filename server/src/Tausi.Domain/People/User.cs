using Tausi.Domain.Common;

namespace Tausi.Domain.People;

public class User : VersionedEntity
{
    /// <summary>Where it was imported from, e.g. "sambapos:menuitem:12:portion:3", so a re-import matches it.</summary>
    public string? ExternalRef { get; set; }

    public string Name { get; set; } = "";

    /// <summary>
    /// The name trimmed and in capitals, kept in step with <see cref="Name"/> when saving. A unique
    /// index on it keeps names unique, ignoring case, across active and inactive users.
    /// </summary>
    public string NormalizedName { get; set; } = "";

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }

    /// <summary>Slow salted hash (PBKDF2) of the PIN, checked after the lookup below.</summary>
    public string PinHash { get; set; } = "";

    /// <summary>
    /// Keyed hash (HMAC with a server secret) of the PIN. It lets login find the user in one
    /// query and lets the database refuse two active users with the same PIN.
    /// </summary>
    public string PinLookup { get; set; } = "";

    /// <summary>
    /// Number of digits in the PIN. Lets the PIN pad submit by itself at the longest length in use,
    /// without revealing any PIN.
    /// </summary>
    public int PinLength { get; set; } = Pin.MaxLength;

    public bool Active { get; set; } = true;
    public bool MustChangePin { get; set; }

    /// <summary>Admin-site sign-in name. Only set for users who may use the admin site.</summary>
    public string? UserName { get; set; }
    public string? PasswordHash { get; set; }

    /// <summary>Wrong admin-site passwords in a row; reset on success.</summary>
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}
