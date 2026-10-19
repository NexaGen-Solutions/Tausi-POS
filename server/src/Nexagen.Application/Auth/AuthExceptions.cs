namespace Nexagen.Application.Auth;

/// <summary>Wrong PIN or password. Becomes HTTP 401.</summary>
public class LoginRejectedException(string message, int? attemptsLeft = null) : Exception(message)
{
    public int? AttemptsLeft { get; } = attemptsLeft;
}

/// <summary>Too many wrong tries; wait until <see cref="Until"/>. Becomes HTTP 429.</summary>
public class LoginLockedException(DateTimeOffset until)
    : Exception("Too many wrong tries. Wait a minute and try again.")
{
    public DateTimeOffset Until { get; } = until;
}
