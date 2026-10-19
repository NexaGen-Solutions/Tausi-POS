using Nexagen.Application.Security;
using Nexagen.Domain.Devices;
using Nexagen.Domain.People;
using Nexagen.Infrastructure.Persistence;
using Nexagen.Infrastructure.Seeding;

namespace Nexagen.Tests.Support;

/// <summary>Seeds the default roles and permission grid and adds users and terminals for tests.</summary>
public static class Builders
{
    public static async Task<Dictionary<string, Role>> SeedRoles(NexagenDbContext db)
    {
        var roles = new Dictionary<string, Role>();
        foreach (var (name, roleKey, adminSite) in DefaultPermissions.Roles)
        {
            var role = new Role { Name = name, SystemKey = roleKey, CanUseAdminSite = adminSite };
            foreach (var key in PermissionKeys.All)
                role.Permissions.Add(new RolePermission { PermissionKey = key, Value = DefaultPermissions.For(name, key) });
            db.Roles.Add(role);
            roles[name] = role;
        }
        await db.SaveChangesAsync();
        return roles;
    }

    public static async Task<User> AddUser(NexagenDbContext db, IPinHasher hasher, string name, Role role, string pin, bool active = true)
    {
        var user = new User
        {
            Name = name,
            RoleId = role.Id,
            PinHash = hasher.Hash(pin),
            PinLookup = hasher.Lookup(pin),
            Active = active,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Terminal> AddTerminal(NexagenDbContext db, string name = "Counter A")
    {
        var terminal = new Terminal { Name = name };
        db.Terminals.Add(terminal);
        await db.SaveChangesAsync();
        return terminal;
    }

    public static SessionUser Session(User user, Role role, Terminal? terminal = null) =>
        new(user.Id, user.Name, role.Id, role.Name, terminal?.Id, terminal?.Name);
}
