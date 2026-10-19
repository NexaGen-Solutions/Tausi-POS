using Nexagen.Domain.Common;

namespace Nexagen.Domain.People;

public class Role : VersionedEntity
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Which built-in role this is (<see cref="RoleKeys"/>), or null for a role a business added.
    /// Stable even if the role is renamed; used for "who may manage whom", never the display name.
    /// </summary>
    public string? SystemKey { get; set; }

    /// <summary>Whether users with this role may sign in to the admin site with a password.</summary>
    public bool CanUseAdminSite { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission : VersionedEntity
{
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public string PermissionKey { get; set; } = "";
    public PermissionValue Value { get; set; }
}
