using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Tausi.Api.Auth;
using Tausi.Application.Auth;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.People;
using Tausi.Infrastructure.Persistence;

namespace Tausi.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record SessionDto(Guid Id, string Name, string Role, Guid? TerminalId, string? TerminalName);

    public sealed record PinLoginResponse(
        string Token, DateTimeOffset ExpiresAt, int IdleLogoutMinutes, SessionDto User, bool MustChangePin);

    public sealed record MeResponse(SessionDto User, IReadOnlyDictionary<string, PermissionValue> Permissions);

    public sealed record SessionStatus(int IdleLogoutMinutes);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/pin", async (PinLoginRequest request, PinLogin login, SessionTokens tokens, CancellationToken ct) =>
        {
            var result = await login.Execute(request, ct);
            var token = tokens.Issue(result.User, result.ExpiresAt);
            return new PinLoginResponse(token, result.ExpiresAt, result.IdleLogoutMinutes, ToDto(result.User), result.MustChangePin);
        });

        // The longest PIN in use, so the pad can submit by itself. Only a number.
        auth.MapGet("/pin/max-length", (GetMaxPinLength get, CancellationToken ct) => get.Execute(ct));

        auth.MapPost("/pin/change", async (ChangePinRequest request, ChangePin change, CancellationToken ct) =>
        {
            await change.Execute(request, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        auth.MapPost("/logout", async (Logout logout, CancellationToken ct) =>
        {
            await logout.Execute(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // Keep-alive for the terminal: any authenticated request refreshes the idle timer.
        auth.MapGet("/session", async (TausiDbContext db, CancellationToken ct) =>
            new SessionStatus((await PosSettings.Load(db, ct)).IdleLogoutMinutes)).RequireAuthorization();

        auth.MapPost("/approve", (IssueApprovalRequest request, IssueApproval approve, CancellationToken ct) =>
            approve.Execute(request, ct));

        auth.MapGet("/me", async (ICurrentSession session, TausiDbContext db, CancellationToken ct) =>
        {
            var user = session.RequireUser();
            var cells = await db.RolePermissions.AsNoTracking()
                .Where(p => p.RoleId == user.RoleId)
                .ToDictionaryAsync(p => p.PermissionKey, p => p.Value, ct);
            var grid = PermissionKeys.All.ToDictionary(k => k, k => cells.GetValueOrDefault(k, PermissionValue.NotAllowed));
            return new MeResponse(ToDto(user), grid);
        }).RequireAuthorization();

        var admin = app.MapGroup("/api/admin").WithTags("Admin site");

        admin.MapPost("/login", async (AdminLoginRequest request, AdminLogin login, HttpContext http, CancellationToken ct) =>
        {
            var user = await login.Execute(request, ct);
            var identity = new ClaimsIdentity(
                [new Claim(TausiClaims.UserId, user.UserId.ToString()), new Claim(TausiClaims.Name, user.Name)],
                TausiClaims.AdminScheme, TausiClaims.Name, null);
            await http.SignInAsync(TausiClaims.AdminScheme, new ClaimsPrincipal(identity));
            return ToDto(user);
        });

        admin.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(TausiClaims.AdminScheme);
            return Results.NoContent();
        });
    }

    private static SessionDto ToDto(SessionUser u) => new(u.UserId, u.Name, u.RoleName, u.TerminalId, u.TerminalName);
}
