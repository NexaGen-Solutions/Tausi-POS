namespace Nexagen.Application.Security;

/// <summary>The user's role may not do this at all. Becomes HTTP 403.</summary>
public class PermissionDeniedException(string permissionKey)
    : Exception($"You are not allowed to do this ({permissionKey}).")
{
    public string PermissionKey { get; } = permissionKey;
}

/// <summary>
/// The user's role may do this with a supervisor's approval, and no valid approval token came
/// with the request. Becomes HTTP 403 with <c>approvalRequired: true</c>.
/// </summary>
public class ApprovalRequiredException(string permissionKey, string? reason = null)
    : Exception(reason ?? $"A supervisor must approve this ({permissionKey}).")
{
    public string PermissionKey { get; } = permissionKey;
}
