using Microsoft.EntityFrameworkCore;
using Tausi.Application.Security;
using Tausi.Infrastructure.Persistence;

namespace Tausi.Api.Auth;

/// <summary>
/// Works out which terminal sent the request from its device token (header, or query string for
/// the SignalR WebSocket). An unknown token is treated as no terminal. Also notes when the terminal
/// was last seen and which app version it runs, and puts a training terminal's requests in training mode.
/// </summary>
public sealed class DeviceTokenMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(1);

    public async Task InvokeAsync(HttpContext http, TausiDbContext db, TimeProvider clock)
    {
        string? token = http.Request.Headers[TausiClaims.DeviceTokenHeader];
        if (string.IsNullOrEmpty(token) && http.Request.Path.StartsWithSegments("/hubs"))
            token = http.Request.Query[TausiClaims.DeviceTokenQuery];

        if (!string.IsNullOrEmpty(token))
        {
            var hash = SecureTokens.Hash(token);
            var terminal = await db.Terminals.AsNoTracking()
                .Where(t => t.DeviceTokenHash == hash)
                .Select(t => new { t.Id, t.Name, t.LastSeenAt, t.AppVersion, t.TrainingMode })
                .FirstOrDefaultAsync(http.RequestAborted);
            if (terminal is not null)
            {
                http.Features.Set(new DeviceTerminal(terminal.Id, terminal.Name, terminal.TrainingMode));
                // A training terminal sees and saves training data only (TausiDbContext.Training).
                db.TrainingMode = terminal.TrainingMode;

                var now = clock.GetUtcNow();
                // The terminal app's version: X-Tausi-Version (sent with every call), or the older X-App-Version.
                string? version = http.Request.Headers[Application.Updates.TerminalVersions.Header].FirstOrDefault()
                    ?? http.Request.Headers[TausiClaims.AppVersionHeader].FirstOrDefault();
                if (terminal.LastSeenAt is null || now - terminal.LastSeenAt > SeenEvery || (version is not null && version != terminal.AppVersion))
                {
                    await db.Terminals.Where(t => t.Id == terminal.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.LastSeenAt, now)
                        .SetProperty(t => t.AppVersion, version ?? terminal.AppVersion), http.RequestAborted);
                }
            }
        }
        await next(http);
    }
}
