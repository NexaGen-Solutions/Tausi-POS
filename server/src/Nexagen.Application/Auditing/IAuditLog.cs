namespace Nexagen.Application.Auditing;

public static class AuditActions
{
    public const string PinLogin = "auth.pin_login";
    public const string PinLoginFailed = "auth.pin_failed";
    public const string TerminalLocked = "auth.terminal_locked";
    public const string AdminLogin = "auth.admin_login";
    public const string AdminLoginFailed = "auth.admin_failed";
    public const string ApprovalIssued = "auth.approval_issued";
    public const string TerminalPaired = "terminal.paired";
    public const string PermissionChanged = "permissions.changed";
    public const string SettingsChanged = "settings.changed";
    public const string LogoChanged = "settings.logo_changed";
    public const string TestPrint = "printer.test";
    public const string DevVoidProbe = "dev.void_probe";
}

/// <summary>
/// One audit entry. User and terminal default to the current session; pass them explicitly when
/// there is no session yet (e.g. a failed login). Old and new values are serialised to JSON.
/// </summary>
public sealed record AuditRecord(
    string Action,
    string? EntityType = null,
    string? EntityId = null,
    object? OldValue = null,
    object? NewValue = null,
    Guid? ApprovedByUserId = null,
    Guid? UserId = null,
    Guid? TerminalId = null);

public interface IAuditLog
{
    /// <summary>
    /// Adds the entry to the current unit of work. It is saved with the caller's
    /// <c>SaveChangesAsync</c>, so the audit and the change it describes commit together.
    /// </summary>
    void Write(AuditRecord record);
}
