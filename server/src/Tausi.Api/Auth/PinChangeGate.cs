using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tausi.Infrastructure.Persistence;

namespace Tausi.Api.Auth;

/// <summary>
/// A user who must choose a new PIN can only do that (or log out) until they have. The server
/// enforces it; the UI just leads them to the change-PIN screen.
/// </summary>
public sealed class PinChangeGate(RequestDelegate next)
{
    private static readonly string[] Allowed =
    [
        "/api/auth/pin/change", "/api/auth/logout", "/api/auth/session", "/api/auth/me",
        "/api/device", "/api/brand", "/api/health", "/hubs",
    ];

    public async Task InvokeAsync(HttpContext http, TausiDbContext db, IProblemDetailsService problems)
    {
        if (http.User.FindFirst(TausiClaims.SessionId) is not null
            && !Allowed.Any(path => http.Request.Path.StartsWithSegments(path))
            && Guid.TryParse(http.User.FindFirst(TausiClaims.UserId)?.Value, out var userId)
            && await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.MustChangePin, http.RequestAborted))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await problems.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                ProblemDetails = new ProblemDetails
                {
                    Status = 403,
                    Title = "Choose a new PIN",
                    Detail = "Choose a new PIN before you continue.",
                    Extensions = { ["code"] = TausiClaims.PinChangeRequiredCode },
                },
            });
            return;
        }
        await next(http);
    }
}
