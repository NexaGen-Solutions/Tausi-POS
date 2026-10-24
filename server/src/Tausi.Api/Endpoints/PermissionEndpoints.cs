using Tausi.Application.Roles;

namespace Tausi.Api.Endpoints;

public static class PermissionEndpoints
{
    public static void MapPermissionEndpoints(this WebApplication app)
    {
        var grid = app.MapGroup("/api/admin/permissions").WithTags("Permissions").RequireAuthorization();
        grid.MapGet("/", (GetPermissions get, CancellationToken ct) => get.Execute(ct));
        grid.MapPut("/", (SavePermissionsRequest request, SavePermissions save, CancellationToken ct) => save.Execute(request, ct));
        grid.MapGet("/history", (PermissionsHistory history, CancellationToken ct) => history.Execute(ct));

        var roles = app.MapGroup("/api/admin/roles").WithTags("Permissions").RequireAuthorization();
        roles.MapPost("/", (CreateRoleRequest request, RoleCommands roles, CancellationToken ct) => roles.Create(request, ct));
        roles.MapPut("/{id:guid}", (Guid id, RenameRoleRequest request, RoleCommands roles, CancellationToken ct) => roles.Rename(id, request, ct));
        roles.MapDelete("/{id:guid}", (Guid id, RoleCommands roles, CancellationToken ct) => roles.Delete(id, ct));
    }
}
