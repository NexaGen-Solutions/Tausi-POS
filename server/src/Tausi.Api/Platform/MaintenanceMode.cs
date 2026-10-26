using Tausi.Application.Backups;

namespace Tausi.Api.Platform;

/// <summary>
/// While a backup is being restored every API and hub call (but health and the backup status)
/// answers 503 "Server is restoring a backup"; terminals show that until it's done.
/// </summary>
public sealed class MaintenanceMode(RequestDelegate next)
{
    public const string Code = "server_restoring";

    private static readonly string[] Open = ["/api/health", "/health", "/api/backups/status"];

    public async Task InvokeAsync(HttpContext http, MaintenanceGate gate)
    {
        var path = http.Request.Path;
        if (gate.Message is { } message && (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs")) && !Open.Any(p => path.StartsWithSegments(p)))
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            http.Response.Headers.RetryAfter = "10";
            await http.Response.WriteAsJsonAsync(new { status = 503, title = "Restoring", code = Code, detail = message }, http.RequestAborted);
            return;
        }
        await next(http);
    }
}
