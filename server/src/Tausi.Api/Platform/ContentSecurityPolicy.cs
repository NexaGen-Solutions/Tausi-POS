namespace Tausi.Api.Platform;

/// <summary>
/// The web pages may only talk to this server (and, inside the terminal app, to the app itself
/// through Tauri's IPC). Together with the app's navigation guard, a terminal can't reach any other
/// address.
/// </summary>
public sealed class ContentSecurityPolicy(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments("/api") && !http.Request.Path.StartsWithSegments("/hubs"))
        {
            var host = http.Request.Host.Value;
            http.Response.OnStarting(() =>
            {
                if (http.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                    http.Response.Headers.ContentSecurityPolicy =
                        "default-src 'self'; " +
                        $"connect-src 'self' ws://{host} wss://{host} ipc: http://ipc.localhost; " +
                        "img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; " +
                        "script-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
                return Task.CompletedTask;
            });
        }
        return next(http);
    }
}
