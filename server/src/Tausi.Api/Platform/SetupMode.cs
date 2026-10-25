using Tausi.Application.Abstractions;
using Tausi.Application.Setup;

namespace Tausi.Api.Platform;

/// <summary>
/// Setup mode (a new install, until the wizard is finished): the API answers 423 "Setup required"
/// except for the wizard's own calls, health, the brand and terminal pairing; the admin site and the
/// POS pages redirect to /setup. The browser that unlocked the wizard (its setup cookie) may use the
/// admin API too, which the wizard's later steps reuse.
/// </summary>
public sealed class SetupMode(RequestDelegate next)
{
    public const string Cookie = "tausi_setup";

    private static readonly string[] Open = ["/api/setup", "/api/health", "/health", "/api/brand", "/api/terminals/pairing", "/api/system"];

    public async Task InvokeAsync(HttpContext http, SetupGate gate, ITausiDb db, SetupFlow flow)
    {
        if (!DatabaseStartup.Ready.Task.IsCompleted || await gate.IsComplete(db, http.RequestAborted))
        {
            await next(http);
            return;
        }
        var path = http.Request.Path;
        if (Open.Any(p => path.StartsWithSegments(p)) || await flow.IsSession(http.Request.Cookies[Cookie], http.RequestAborted))
        {
            await next(http);
            return;
        }
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs"))
        {
            http.Response.StatusCode = StatusCodes.Status423Locked;
            await http.Response.WriteAsJsonAsync(new
            {
                status = 423, title = "Setup required", code = SetupCodes.Required,
                detail = "This server isn't set up yet. Open /setup on the server to finish setting it up.",
            }, http.RequestAborted);
            return;
        }
        if (HttpMethods.IsGet(http.Request.Method) && (path.StartsWithSegments("/admin") || path.StartsWithSegments("/pos")))
        {
            http.Response.Redirect("/setup");
            return;
        }
        await next(http);
    }
}

/// <summary>Endpoints that wait for a licence key once the licence is past its grace period.</summary>
public static class LicenceGateExtensions
{
    public static TBuilder RequiresLicence<TBuilder>(this TBuilder builder, LicensedFeature feature)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            await context.HttpContext.RequestServices.GetRequiredService<LicenceService>().Require(feature, context.HttpContext.RequestAborted);
            return await next(context);
        });
}
