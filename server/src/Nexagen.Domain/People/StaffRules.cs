namespace Nexagen.Domain.People;

/// <summary>Keys of the four built-in roles (<see cref="Role.SystemKey"/>).</summary>
public static class RoleKeys
{
    public const string Waiter = "waiter";
    public const string Cashier = "cashier";
    public const string Supervisor = "supervisor";
    public const string Admin = "admin";
}

/// <summary>How much a role may do on the staff screen, and how protected its holders are.</summary>
public enum StaffTier
{
    /// <summary>Waiter, Cashier, and added roles without staff.manage.</summary>
    Staff,

    /// <summary>Supervisor, and added roles that have staff.manage.</summary>
    Manager,

    /// <summary>Admin.</summary>
    Admin,
}

/// <summary>
/// Who may manage whom on the staff screen (on top of needing staff.manage):
/// a Manager manages Staff only (not other managers, not admins, not themselves);
/// an Admin manages everyone, but cannot change their own role or deactivate themselves,
/// and the last active Admin cannot be deactivated.
/// </summary>
public static class StaffRules
{
    public static StaffTier TierOf(string? systemKey, bool hasStaffManage) => systemKey switch
    {
        RoleKeys.Admin => StaffTier.Admin,
        RoleKeys.Supervisor => StaffTier.Manager,
        RoleKeys.Waiter or RoleKeys.Cashier => StaffTier.Staff,
        _ => hasStaffManage ? StaffTier.Manager : StaffTier.Staff,
    };

    /// <summary>May the actor create, edit, reset or deactivate someone in the target tier?</summary>
    public static bool CanManage(StaffTier actor, StaffTier target) =>
        actor == StaffTier.Admin || (actor == StaffTier.Manager && target == StaffTier.Staff);

    /// <summary>May the actor give someone a role of this tier?</summary>
    public static bool CanAssign(StaffTier actor, StaffTier role) => CanManage(actor, role);
}
