using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;

namespace Tausi.Application.Roles;

public sealed record PermissionKeyDto(string Key, string Section, string Label, string Help, bool Approvable);

public sealed record RoleDto(Guid Id, string Name, string? SystemKey, IReadOnlyList<string> Users, long RowVersion);

/// <summary>
/// The permissions screen: roles (columns), keys (rows, in sections) and every cell.
/// <c>Version</c> changes whenever a role or cell does; a save sends it back.
/// </summary>
public sealed record PermissionsGridDto(
    string Version, IReadOnlyList<RoleDto> Roles, IReadOnlyList<PermissionKeyDto> Keys,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, PermissionValue>> Cells);

public sealed record PermissionCellChange(Guid RoleId, string PermissionKey, PermissionValue Value);

public sealed record SavePermissionsRequest(string Version, List<PermissionCellChange>? Changes);

public sealed record CreateRoleRequest(string Name, Guid? CopyFromRoleId);

public sealed record RenameRoleRequest(string Name, long RowVersion);

public sealed record PermissionChangeDto(DateTimeOffset At, string Who, string What, string? Role, string? Key, string? Old, string? New);

public static class PermissionCodes
{
    public const string Conflict = "permissions_conflict";
    public const string RoleInUse = "role_in_use";
    public const string BuiltInRole = "role_built_in";
    public const string NameTaken = "name_taken";
}

/// <summary>Reads the grid and checks a grid before it is saved.</summary>
public sealed class PermissionGrid(ITausiDb db)
{
    public async Task<PermissionsGridDto> Read(CancellationToken ct)
    {
        // The built-in roles first, in their usual order, then added ones by name.
        var roles = (await db.Roles.AsNoTracking().ToListAsync(ct))
            .OrderBy(r => Array.IndexOf(BuiltIn, r.SystemKey) is var i && i >= 0 ? i : BuiltIn.Length).ThenBy(r => r.Name).ToList();
        var cells = await db.RolePermissions.AsNoTracking().ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Where(u => u.Active).OrderBy(u => u.Name)
            .Select(u => new { u.RoleId, u.Name }).ToListAsync(ct);

        var grid = roles.ToDictionary(r => r.Id, r => (IReadOnlyDictionary<string, PermissionValue>)PermissionKeys.All.ToDictionary(
            k => k, k => cells.FirstOrDefault(c => c.RoleId == r.Id && c.PermissionKey == k)?.Value ?? PermissionValue.NotAllowed));
        return new PermissionsGridDto(
            Version(roles, grid),
            roles.Select(r => new RoleDto(r.Id, r.Name, r.SystemKey, users.Where(u => u.RoleId == r.Id).Select(u => u.Name).ToList(), r.RowVersion)).ToList(),
            PermissionCatalog.All.Select(p => new PermissionKeyDto(p.Key, p.Section, p.Label, p.Help, p.Approvable)).ToList(),
            grid);
    }

    private static readonly string?[] BuiltIn = [RoleKeys.Waiter, RoleKeys.Cashier, RoleKeys.Supervisor, RoleKeys.Admin];

    /// <summary>A short hash of every role name and cell: any change by anyone gives a new version.</summary>
    private static string Version(IEnumerable<Role> roles, IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, PermissionValue>> grid)
    {
        var text = new StringBuilder();
        foreach (var role in roles.OrderBy(r => r.Id))
        {
            text.Append(role.Id).Append('|').Append(role.Name).Append('\n');
            foreach (var (key, value) in grid[role.Id].OrderBy(c => c.Key, StringComparer.Ordinal))
                text.Append(key).Append('=').Append((int)value).Append(';');
            text.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Why a grid can't be saved: nobody active left with permissions.manage or staff.manage, or a
    /// key that needs approval for some role with no role allowed to approve it. Empty when it is fine.
    /// </summary>
    public static Dictionary<string, string> Problems(
        IReadOnlyDictionary<Guid, Dictionary<string, PermissionValue>> grid, IReadOnlyCollection<Guid> rolesWithActiveUsers)
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, what) in new[] { (PermissionKeys.PermissionsManage, "change permissions"), (PermissionKeys.StaffManage, "manage staff") })
            if (!grid.Any(r => rolesWithActiveUsers.Contains(r.Key) && r.Value.GetValueOrDefault(key) == PermissionValue.Allowed))
                problems[key] = $"Nobody active could {what} any more. Keep it Allowed for a role that has staff.";
        foreach (var info in PermissionCatalog.All.Where(p => p.Approvable))
        {
            var needs = grid.Values.Any(cells => cells.GetValueOrDefault(info.Key) == PermissionValue.NeedsApproval);
            if (needs && !grid.Values.Any(cells => cells.GetValueOrDefault(info.Key) == PermissionValue.Allowed))
                problems[info.Key] = $"Nobody can approve \"{info.Label}\". Allow it for at least one role.";
        }
        return problems;
    }
}

public sealed class GetPermissions(ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval, PermissionGrid grid)
{
    public async Task<PermissionsGridDto> Execute(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.PermissionsManage, approval.ApprovalToken, ct);
        return await grid.Read(ct);
    }
}

/// <summary>
/// Saves staged cell changes at once, if nobody else saved since the screen loaded. Refused when a
/// key that can't be approved is set to NeedsApproval, when the Admin role's permissions.manage is
/// changed, or when the result would lock everyone out (see <see cref="PermissionGrid.Problems"/>).
/// One audit entry per changed cell, then <c>permissions.changed</c>; the next request uses the new grid.
/// </summary>
public sealed class SavePermissions(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    PermissionGrid grid, IAuditLog audit, IPosEvents events)
{
    public async Task<PermissionsGridDto> Execute(SavePermissionsRequest request, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.PermissionsManage, approval.ApprovalToken, ct);
        var current = await grid.Read(ct);
        if (current.Version != request.Version)
            throw new ConflictException("Someone else saved permissions. Reload to see their changes.", PermissionCodes.Conflict);

        var roles = current.Roles.ToDictionary(r => r.Id);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var changes = request.Changes ?? [];
        foreach (var change in changes)
        {
            var cell = $"{change.RoleId}:{change.PermissionKey}";
            if (!roles.TryGetValue(change.RoleId, out var role)) errors[cell] = "That role no longer exists.";
            else if (!PermissionKeys.IsKnown(change.PermissionKey)) errors[cell] = "Unknown permission.";
            else if (!Enum.IsDefined(change.Value)) errors[cell] = "Choose Allowed, Needs approval or Not allowed.";
            else if (change.Value == PermissionValue.NeedsApproval && !PermissionCatalog.IsApprovable(change.PermissionKey))
                errors[cell] = $"{PermissionCatalog.Find(change.PermissionKey)!.Label} can't be approved: choose Allowed or Not allowed.";
            else if (PermissionCatalog.IsLocked(role.SystemKey, change.PermissionKey) && change.Value != PermissionValue.Allowed)
                errors[cell] = "Kept so you can't lock yourself out.";
        }
        if (errors.Count > 0) throw new FieldErrorsException(errors, errors.Values.First());

        var next = current.Cells.ToDictionary(r => r.Key, r => r.Value.ToDictionary(c => c.Key, c => c.Value));
        foreach (var change in changes) next[change.RoleId][change.PermissionKey] = change.Value;
        var withUsers = current.Roles.Where(r => r.Users.Count > 0).Select(r => r.Id).ToList();
        var problems = PermissionGrid.Problems(next, withUsers);
        if (problems.Count > 0) throw new FieldErrorsException(problems, problems.Values.First());

        var changed = new List<Guid>();
        foreach (var change in changes.DistinctBy(c => (c.RoleId, c.PermissionKey)))
        {
            var old = current.Cells[change.RoleId][change.PermissionKey];
            var value = next[change.RoleId][change.PermissionKey];
            if (old == value) continue;
            var row = await db.RolePermissions.FirstOrDefaultAsync(p => p.RoleId == change.RoleId && p.PermissionKey == change.PermissionKey, ct);
            if (row is null) db.RolePermissions.Add(new RolePermission { RoleId = change.RoleId, PermissionKey = change.PermissionKey, Value = value });
            else row.Value = value;
            var name = roles[change.RoleId].Name;
            audit.Write(new AuditRecord(AuditActions.PermissionChanged, AuditEntities.Role, change.RoleId.ToString(),
                new { role = name, permission = change.PermissionKey, value = old },
                new { role = name, permission = change.PermissionKey, value }, grant.ApprovedByUserId));
            changed.Add(change.RoleId);
        }
        if (changed.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await events.Publish(PosEventNames.PermissionsChanged, new { roles = changed.Distinct() }, ct);
        }
        return await grid.Read(ct);
    }
}

/// <summary>Adds, renames and deletes roles (permissions.manage). A new role can copy another's permissions.</summary>
public sealed class RoleCommands(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval,
    PermissionGrid grid, IAuditLog audit, IPosEvents events)
{
    public const int MaxNameLength = 40;

    public async Task<PermissionsGridDto> Create(CreateRoleRequest request, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.PermissionsManage, approval.ApprovalToken, ct);
        var name = await CheckName(request.Name, null, ct);
        var source = request.CopyFromRoleId is { } from
            ? await db.Roles.AsNoTracking().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == from, ct)
              ?? throw new DomainException("The role to copy from no longer exists.")
            : null;

        var role = new Role { Name = name, CanUseAdminSite = source?.CanUseAdminSite ?? false };
        db.Roles.Add(role);
        foreach (var cell in source?.Permissions ?? [])
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionKey = cell.PermissionKey, Value = cell.Value });
        audit.Write(new AuditRecord(AuditActions.RoleCreated, AuditEntities.Role, role.Id.ToString(), null,
            new { role = name, copiedFrom = source?.Name }, grant.ApprovedByUserId));
        return await Saved(ct);
    }

    public async Task<PermissionsGridDto> Rename(Guid id, RenameRoleRequest request, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.PermissionsManage, approval.ApprovalToken, ct);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Role not found.");
        if (role.RowVersion != request.RowVersion)
            throw new ConflictException("Someone else saved permissions. Reload to see their changes.", PermissionCodes.Conflict);
        var name = await CheckName(request.Name, id, ct);
        if (name == role.Name) return await grid.Read(ct);
        audit.Write(new AuditRecord(AuditActions.RoleRenamed, AuditEntities.Role, role.Id.ToString(),
            new { role = role.Name }, new { role = name }, grant.ApprovedByUserId));
        role.Name = name;
        return await Saved(ct);
    }

    /// <summary>Only a role nobody has (active or not); the four built-in roles stay.</summary>
    public async Task<PermissionsGridDto> Delete(Guid id, CancellationToken ct)
    {
        var grant = await permissions.Require(session.RequireUser(), PermissionKeys.PermissionsManage, approval.ApprovalToken, ct);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Role not found.");
        if (role.SystemKey is not null)
            throw new ConflictException($"{role.Name} is a built-in role and can't be deleted. You can rename it.", PermissionCodes.BuiltInRole);
        var users = await db.Users.AsNoTracking().Where(u => u.RoleId == id).OrderBy(u => u.Name).Select(u => u.Name).ToListAsync(ct);
        if (users.Count > 0)
            throw new ConflictException(
                $"{string.Join(", ", users)} {(users.Count == 1 ? "has" : "have")} this role. Give them another role in Staff first.",
                PermissionCodes.RoleInUse);

        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(p => p.RoleId == id).ToListAsync(ct));
        db.Roles.Remove(role);
        audit.Write(new AuditRecord(AuditActions.RoleDeleted, AuditEntities.Role, role.Id.ToString(),
            new { role = role.Name }, null, grant.ApprovedByUserId));
        return await Saved(ct);
    }

    private async Task<string> CheckName(string? name, Guid? except, CancellationToken ct)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) throw new DomainException("Give the role a name.", "name_required");
        if (clean.Length > MaxNameLength) throw new DomainException($"Use at most {MaxNameLength} characters.", "name_too_long");
        var lower = clean.ToLower();
        if (await db.Roles.AnyAsync(r => r.Id != except && r.Name.ToLower() == lower, ct))
            throw new DomainException($"There is already a role called {clean}.", PermissionCodes.NameTaken);
        return clean;
    }

    private async Task<PermissionsGridDto> Saved(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await events.Publish(PosEventNames.PermissionsChanged, new { }, ct);
        return await grid.Read(ct);
    }
}

/// <summary>The last 50 permission and role changes: who, when, the cell, and the old and new value.</summary>
public sealed class PermissionsHistory(ITausiDb db, ICurrentSession session, IPermissionService permissions, IApprovalTokenSource approval)
{
    private static readonly string[] Actions =
        [AuditActions.PermissionChanged, AuditActions.RoleCreated, AuditActions.RoleRenamed, AuditActions.RoleDeleted];

    public async Task<IReadOnlyList<PermissionChangeDto>> Execute(CancellationToken ct)
    {
        await permissions.Require(session.RequireUser(), PermissionKeys.PermissionsManage, approval.ApprovalToken, ct);
        var entries = await db.AuditEntries.AsNoTracking().Where(a => Actions.Contains(a.Action))
            .OrderByDescending(a => a.At).Take(50).ToListAsync(ct);
        var userIds = entries.Where(e => e.UserId != null).Select(e => e.UserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return entries.Select(e =>
        {
            var old = Parse(e.OldValueJson);
            var now = Parse(e.NewValueJson);
            var who = e.UserId is { } id ? names.GetValueOrDefault(id, "?") : "?";
            return e.Action switch
            {
                AuditActions.PermissionChanged => new PermissionChangeDto(e.At, who, "Permission",
                    Text(now?["role"] ?? old?["role"]), Text(now?["permission"]), Value(old?["value"]), Value(now?["value"])),
                AuditActions.RoleCreated => new PermissionChangeDto(e.At, who, "Role added", Text(now?["role"]), null, null,
                    now?["copiedFrom"] is { } from ? $"Copied from {Text(from)}" : null),
                AuditActions.RoleRenamed => new PermissionChangeDto(e.At, who, "Role renamed", Text(now?["role"]), null,
                    Text(old?["role"]), Text(now?["role"])),
                _ => new PermissionChangeDto(e.At, who, "Role deleted", Text(old?["role"]), null, null, null),
            };
        }).ToList();
    }

    private static JsonObject? Parse(string? json) => string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;

    private static string? Text(JsonNode? node) => node?.GetValue<string>();

    /// <summary>Values were saved as names ("Allowed") or, by older code, as numbers.</summary>
    private static string? Value(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<int>(out var i) => ((PermissionValue)i).ToString(),
        _ => node.ToJsonString(),
    };
}
