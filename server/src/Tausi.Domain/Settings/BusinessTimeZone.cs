namespace Tausi.Domain.Settings;

/// <summary>Business time zones by IANA name ("Africa/Nairobi"), on Linux and Windows alike.</summary>
public static class BusinessTimeZone
{
    public const int MaxLength = 64;

    /// <summary>The server's own time zone as an IANA name (Windows ids are converted).</summary>
    public static string ServerZoneId() => IanaId(TimeZoneInfo.Local);

    public static string IanaId(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana
        : zone.Id;

    /// <summary>The zone for a stored name; empty means the server's.</summary>
    public static bool TryFind(string? name, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Local;
        if (string.IsNullOrWhiteSpace(name)) return true;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(name.Trim());
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The zone for a stored name; the server's when it is empty or unknown on this machine.</summary>
    public static TimeZoneInfo Find(string? name) => TryFind(name, out var zone) ? zone : TimeZoneInfo.Local;

    /// <summary>An IANA name that this machine knows (so it works on both Linux and Windows servers).</summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxLength && TryFind(name, out var zone) && zone.HasIanaId;
}
