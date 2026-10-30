using Tausi.Domain.Common;

namespace Tausi.Domain.Platform;

public class IdempotencyRecord : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public string Key { get; set; } = "";

    /// <summary>Who sent it: a user id, a terminal id, or "anonymous".</summary>
    public string Caller { get; set; } = "";

    /// <summary>Method and path, e.g. "POST /api/auth/approve".</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>SHA-256 of the request body, to spot a reused key with a different request.</summary>
    public string RequestHash { get; set; } = "";

    public bool Completed { get; set; }

    /// <summary>The request's changes committed. Saved with them, so it is true only if they are in the database.</summary>
    public bool Ran { get; set; }

    public int ResponseStatus { get; set; }
    public string? ResponseContentType { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
