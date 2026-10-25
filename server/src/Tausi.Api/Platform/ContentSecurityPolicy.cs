namespace Tausi.Api.Platform;

/// <summary>
/// Security headers on every answer: no framing (X-Frame-Options and frame-ancestors), no MIME
/// sniffing, no referrer sent elsewhere, and a Content-Security-Policy. The web pages may only talk
/// to this server (and, inside the terminal app, to the app itself through Tauri's IPC); together with
/// the app's navigation guard a terminal can't reach any other address. API answers may load nothing
/// at all; files (images, PDFs opened in the browser's viewer) may only not be framed.
/// </summary>
public sealed class ContentSecurityPolicy(RequestDelegate next)
{
    public const string ApiPolicy = "default-src 'none'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext http)
    {
        var host = http.Request.Host.Value;
        http.Response.OnStarting(() =>
        {
            var headers = http.Response.Headers;
            headers.XFrameOptions = "DENY";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            var type = http.Response.ContentType ?? "";
            headers.ContentSecurityPolicy =
                type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                    ? "default-src 'self'; " +
                      $"connect-src 'self' ws://{host} wss://{host} ipc: http://ipc.localhost; " +
                      "img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; " +
                      "script-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'"
                    : type.Contains("json", StringComparison.OrdinalIgnoreCase) || type.Length == 0 ? ApiPolicy : "frame-ancestors 'none'";
            return Task.CompletedTask;
        });
        return next(http);
    }
}
