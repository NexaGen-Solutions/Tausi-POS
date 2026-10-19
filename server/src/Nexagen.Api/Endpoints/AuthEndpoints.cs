using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Nexagen.Api.Auth;
using Nexagen.Application.Auth;
using Nexagen.Application.Security;
using Nexagen.Domain.People;

namespace Nexagen.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record SessionDto(Guid Id, string Name, string Role, Guid? TerminalId, string? TerminalName);

    public sealed record PinLoginResponse(string Token, DateTimeOffset ExpiresAt, SessionDto User, bool MustChangePin);

    public sealed record MeResponse(SessionDto User, IReadOnlyDictionary<string, PermissionValue> Permissions);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/pin", async (PinLoginRequest request, PinLogin login, SessionTokens tokens, CancellationToken ct) =>
        {
            var result = await login.Execute(request, ct);
            var (token, expires) = tokens.Issue(result.User);
            return new PinLoginResponse(token, expires, ToDto(result.User), result.MustChangePin);
        });

        auth.MapPost("/approve", (IssueApprovalRequest request, IssueApproval approve, CancellationToken ct) =>
            approve.Execute(request, ct));

        auth.MapGet("/me", async (ICurrentSession session, IPermissionService permissions, CancellationToken ct) =>
        {
            var user = session.RequireUser();
            var grid = new Dictionary<string, PermissionValue>();
            foreach (var key in PermissionKeys.All) grid[key] = await permissions.Check(user, key, ct);
            return new MeResponse(ToDto(user), grid);
        }).RequireAuthorization();

        var admin = app.MapGroup("/api/admin").WithTags("Admin site");

        admin.MapPost("/login", async (AdminLoginRequest request, AdminLogin login, HttpContext http, CancellationToken ct) =>
        {
            var user = await login.Execute(request, ct);
            var identity = new ClaimsIdentity(
                [new Claim(NexagenClaims.UserId, user.UserId.ToString()), new Claim(NexagenClaims.Name, user.Name)],
                NexagenClaims.AdminScheme, NexagenClaims.Name, null);
            await http.SignInAsync(NexagenClaims.AdminScheme, new ClaimsPrincipal(identity));
            return ToDto(user);
        });

        admin.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(NexagenClaims.AdminScheme);
            return Results.NoContent();
        });
    }

    private static SessionDto ToDto(SessionUser u) => new(u.UserId, u.Name, u.RoleName, u.TerminalId, u.TerminalName);
}
