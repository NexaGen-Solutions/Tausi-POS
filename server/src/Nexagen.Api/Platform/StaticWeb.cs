using Microsoft.Extensions.FileProviders;

namespace Nexagen.Api.Platform;

/// <summary>
/// Serves the built web app (terminal screens and admin site), so production needs only this
/// server. Unknown non-API paths fall back to index.html for client-side routing.
/// </summary>
public static class StaticWeb
{
    private static string Root(WebApplication app) =>
        Path.GetFullPath(app.Configuration["Web:Root"] ?? "wwwroot", app.Environment.ContentRootPath);

    private static bool Built(WebApplication app) => File.Exists(Path.Combine(Root(app), "index.html"));

    public static void UseNexagenStaticFiles(this WebApplication app)
    {
        if (!Built(app))
        {
            app.Logger.LogWarning("Web app not found at {Root}; build it with `npm run build` in web/", Root(app));
            return;
        }
        app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(Root(app)) });
    }

    public static void MapNexagenWebFallback(this WebApplication app)
    {
        foreach (var prefix in new[] { "/api", "/hubs", "/files" })
            app.MapFallback(prefix + "/{**rest}", () => Results.NotFound());
        if (Built(app))
            app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = new PhysicalFileProvider(Root(app)) });
    }
}
