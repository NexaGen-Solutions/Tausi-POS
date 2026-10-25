using Tausi.Domain.Common;

namespace Tausi.Domain.Settings;

/// <summary>
/// The business's licence (one row): a signed key, checked offline, or a 30-day trial. The server
/// remembers the latest time it has seen (<see cref="LastSeenAt"/>), so a clock set back doesn't
/// make a licence last longer.
/// </summary>
public class Licence : VersionedEntity
{
    public const int TrialDays = 30;
    public const int TrialTerminals = 2;

    /// <summary>The key as entered ("TAUSI-…"); empty for a trial.</summary>
    public string Key { get; set; } = "";
    public string? LicenceId { get; set; }
    public string BusinessName { get; set; } = "";
    public string Edition { get; set; } = "";
    public int TerminalLimit { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    /// <summary>Null for a perpetual licence.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? SupportUntil { get; set; }
    public bool IsTrial { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    /// <summary>The latest time the server has seen; "now" is never earlier than this.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }
}

public enum LicenceStatus
{
    /// <summary>No key and no trial yet (only during setup).</summary>
    None,
    Trial,
    Active,
    /// <summary>Within 14 days of the expiry.</summary>
    ExpiresSoon,
    /// <summary>Expired, within the 14-day grace period.</summary>
    Grace,
    /// <summary>Past the grace period: pairing and the Menu, Stock, Reports and Settings pages wait for a key.</summary>
    Expired,
}

/// <summary>
/// Never stops a bar mid-service: selling, printing, settling and closing the day always work.
/// From 14 days before the expiry a banner warns; after it 14 days of grace; after that only
/// pairing and the admin pages for Menu, Stock, Reports and Settings (but the licence) wait for a key.
/// </summary>
public static class LicencePolicy
{
    public static readonly TimeSpan Warning = TimeSpan.FromDays(14);
    public static readonly TimeSpan Grace = TimeSpan.FromDays(14);

    /// <summary>The time the licence is judged at: the clock, or the latest time seen if the clock went back.</summary>
    public static DateTimeOffset Now(Licence? licence, DateTimeOffset clock) =>
        licence?.LastSeenAt is { } seen && seen > clock ? seen : clock;

    public static LicenceStatus Status(Licence? licence, DateTimeOffset clock)
    {
        if (licence is null) return LicenceStatus.None;
        var now = Now(licence, clock);
        if (licence.ExpiresAt is not { } expires) return licence.IsTrial ? LicenceStatus.Trial : LicenceStatus.Active;
        if (now < expires - Warning) return licence.IsTrial ? LicenceStatus.Trial : LicenceStatus.Active;
        if (now < expires) return licence.IsTrial ? LicenceStatus.Trial : LicenceStatus.ExpiresSoon;
        return now < expires + Grace ? LicenceStatus.Grace : LicenceStatus.Expired;
    }

    /// <summary>Whole days left before the expiry (0 on the last day); null for a perpetual licence.</summary>
    public static int? DaysLeft(Licence? licence, DateTimeOffset clock) =>
        licence?.ExpiresAt is { } expires ? Math.Max(0, (int)Math.Ceiling((expires - Now(licence, clock)).TotalDays)) : null;
}
