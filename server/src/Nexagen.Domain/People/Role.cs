using Nexagen.Domain.Common;

namespace Nexagen.Domain.People;

public class Role : VersionedEntity
{
    public string Name { get; set; } = "";

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
