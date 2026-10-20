using Tausi.Domain.Common;

namespace Tausi.Domain.Devices;

/// <summary>
/// A pairing in progress. The terminal app shows <see cref="Code"/>; an admin enters it to bind the
/// app to a terminal; the app then claims its device token with the secret only it holds.
/// </summary>
public class TerminalPairing : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public string Code { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public Guid? TerminalId { get; set; }

    /// <summary>The device token, held only between the admin pairing and the app claiming it.</summary>
    public string? PendingDeviceToken { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
}
