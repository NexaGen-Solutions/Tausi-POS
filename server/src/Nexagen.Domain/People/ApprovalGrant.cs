using Nexagen.Domain.Common;

namespace Nexagen.Domain.People;

/// <summary>A single-use supervisor approval for one permission key, valid for a short time.</summary>
public class ApprovalGrant : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public string TokenHash { get; set; } = "";
    public string PermissionKey { get; set; } = "";
    public Guid ApprovedByUserId { get; set; }
    public Guid? TerminalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByUserId { get; set; }

    public bool IsUsable(string permissionKey, DateTimeOffset now) =>
        UsedAt is null && ExpiresAt > now && PermissionKey == permissionKey;
}
