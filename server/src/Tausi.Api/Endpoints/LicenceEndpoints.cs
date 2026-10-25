using Tausi.Application.Setup;

namespace Tausi.Api.Endpoints;

public static class LicenceEndpoints
{
    public static void MapLicenceEndpoints(this WebApplication app)
    {
        // The banners: anyone signed in (admin site or a terminal) may read the licence state.
        app.MapGet("/api/licence", (LicenceService licences, CancellationToken ct) => licences.Status(ct))
            .WithTags("Licence").RequireAuthorization();

        // Settings → Licence: never gated, so an expired install can always enter a new key.
        var admin = app.MapGroup("/api/admin/licence").WithTags("Licence").RequireAuthorization();
        admin.MapPost("/", (EnterLicenceRequest r, LicenceService licences, CancellationToken ct) => licences.Enter(r, asSetup: false, ct));
        admin.MapPost("/trial", (LicenceService licences, CancellationToken ct) => licences.StartTrial(asSetup: false, ct));
    }
}
