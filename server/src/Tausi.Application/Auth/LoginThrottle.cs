using System.Collections.Concurrent;

namespace Tausi.Application.Auth;

/// <summary>
/// Admin-site sign-in limits: <see cref="MaxFailures"/> wrong passwords within <see cref="Window"/> lock that
/// user name, and separately that IP address, until the oldest of those failures is <see cref="Window"/> old.
/// Kept in memory: a restart clears it, which needs someone at the server anyway.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();

    public static string UserKey(string userName) => "user:" + userName.Trim().ToLowerInvariant();
    public static string IpKey(string? ip) => "ip:" + (ip ?? "unknown");

    /// <summary>When the first of these keys that is locked unlocks; null when none is.</summary>
    public DateTimeOffset? LockedUntil(params string[] keys)
    {
        var now = clock.GetUtcNow();
        DateTimeOffset? until = null;
        foreach (var key in keys)
        {
            if (!_failures.TryGetValue(key, out var list)) continue;
            lock (list)
            {
                list.RemoveAll(t => t <= now - Window);
                if (list.Count >= MaxFailures)
                {
                    var unlocks = list[^MaxFailures] + Window;
                    if (until is null || unlocks > until) until = unlocks;
                }
            }
        }
        return until;
    }

    public void Fail(params string[] keys)
    {
        var now = clock.GetUtcNow();
        foreach (var key in keys)
        {
            var list = _failures.GetOrAdd(key, _ => []);
            lock (list) list.Add(now);
        }
    }

    public void Clear(string key) => _failures.TryRemove(key, out _);
}
