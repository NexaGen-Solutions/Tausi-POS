namespace Nexagen.Application.Staff;

public sealed record StaffRoleDto(Guid Id, string Name, bool CanUseAdminSite);

public sealed record LastLoginDto(DateTimeOffset At, string? TerminalName);

public sealed record StaffMemberDto(
    Guid Id,
    string Name,
    Guid RoleId,
    string RoleName,
    bool Active,
    bool MustChangePin,
    string? AdminUserName,
    bool HasAdminPassword,
    LastLoginDto? LastLogin,
    IReadOnlyList<string> TerminalsLast7Days,
    long RowVersion);

/// <summary>The people this manager may see and manage, and the roles they may give.</summary>
public sealed record StaffListDto(IReadOnlyList<StaffMemberDto> Members, IReadOnlyList<StaffRoleDto> AssignableRoles);

/// <summary>
/// After create, reset PIN and reactivate. <see cref="TemporaryPin"/> is the only time the PIN is
/// ever shown; it is not stored anywhere except as a hash.
/// </summary>
public sealed record StaffSaved(StaffMemberDto Member, string? TemporaryPin);

public sealed record CreateStaffRequest(string Name, Guid RoleId, string StartingPin, string? AdminPassword);

/// <summary><c>AdminPassword</c> null keeps the current one; a value sets or resets it.</summary>
public sealed record UpdateStaffRequest(string Name, Guid RoleId, string? AdminPassword, long RowVersion);

/// <summary>Give <c>NewPin</c>, or set <c>Generate</c> for a random valid one.</summary>
public sealed record SetPinRequest(string? NewPin, bool Generate, long RowVersion);

public sealed record RowVersionRequest(long RowVersion);

public static class StaffCodes
{
    public const string NotManageable = "staff_not_manageable";
    public const string RoleNotAssignable = "role_not_assignable";
    public const string OwnRole = "own_role";
    public const string SelfDeactivate = "self_deactivate";
    public const string LastAdmin = "last_admin";
    public const string NameTaken = "name_taken";
    public const string NameInvalid = "name_invalid";
    public const string PasswordRequired = "password_required";
    public const string PasswordTooShort = "password_too_short";
    public const string Stale = "stale";

    public const string StaleMessage = "This person was changed by someone else. Reload and try again.";
}
