namespace Nexagen.Domain.People;

/// <summary>
/// Wrong-PIN rule for a terminal: <see cref="MaxFailures"/> wrong PINs within <see cref="Window"/>
/// lock that terminal for <see cref="LockFor"/>.
/// </summary>
public static class LoginLockout
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan LockFor = TimeSpan.FromSeconds(60);

    public static bool ShouldLock(IEnumerable<DateTimeOffset> recentFailures, DateTimeOffset now) =>
        recentFailures.Count(t => t > now - Window && t <= now) >= MaxFailures;
}
