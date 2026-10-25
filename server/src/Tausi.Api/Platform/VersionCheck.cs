using Tausi.Api.Auth;
using Tausi.Application.Updates;
using Tausi.Api.Endpoints;

namespace Tausi.Api.Platform;

/// <summary>
/// Every API answer says the server's version (X-Tausi-Server-Version). A terminal app older than the
/// oldest this server works with (its X-Tausi-Version) can't log in: it gets 426 and updates first.
/// </summary>
public sealed class VersionCheck(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments("/api"))
        {
            await next(http);
            return;
        }
        http.Response.Headers[TerminalVersions.ServerHeader] = SystemEndpoints.Version;
        if (http.Request.Path.StartsWithSegments("/api/auth/pin")
            && http.Request.Headers.ContainsKey(TausiClaims.DeviceTokenHeader)
            && TerminalVersions.TooOld(http.Request.Headers[TerminalVersions.Header]))
        {
            http.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await http.Response.WriteAsJsonAsync(new
            {
                status = 426, title = "Update required", code = TerminalVersions.UpdateRequiredCode,
                detail = "Updating, please wait.", minimum = TerminalVersions.Minimum,
            }, http.RequestAborted);
            return;
        }
        await next(http);
    }
}
