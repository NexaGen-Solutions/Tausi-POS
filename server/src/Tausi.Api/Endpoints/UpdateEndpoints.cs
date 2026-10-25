using Tausi.Api.Platform;
using Tausi.Application.Updates;

namespace Tausi.Api.Endpoints;

public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this WebApplication app)
    {
        // Settings → System.
        var system = app.MapGroup("/api/admin/system").WithTags("System").RequireAuthorization();
        system.MapGet("/", (ServerUpdates u, CancellationToken ct) => u.Info(ct));
        system.MapGet("/updates", (ServerUpdates u, CancellationToken ct) => u.Check(ct));
        system.MapPost("/updates/apply", async (ApplyUpdateRequest r, ServerUpdates u, CancellationToken ct) =>
        {
            await u.Apply(r, ct);
            return Results.Accepted();
        });
        system.MapPost("/updates/upload", async (IFormFile file, bool? force, string? confirm, ServerUpdates u, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            await u.Upload(stream, new ApplyUpdateRequest(force ?? false, confirm), ct);
            return Results.Accepted();
        }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(600L * 1024 * 1024));

        // The terminals' update source, in the Tauri updater's format. No sign-in: the files are signed.
        var terminal = app.MapGroup("/api/updates/terminal").WithTags("Updates");
        terminal.MapGet("/latest.json", (HttpContext http, TerminalUpdateFeed feed) =>
        {
            var update = feed.Offer(SystemEndpoints.Version);
            if (update is null) return Results.NoContent();
            var url = $"{http.Request.Scheme}://{http.Request.Host}/api/updates/terminal/files/{Uri.EscapeDataString(update.File)}";
            return Results.Json(new
            {
                version = update.Version,
                notes = update.Notes ?? "",
                pub_date = update.PublishedAt,
                platforms = new Dictionary<string, object> { ["windows-x86_64"] = new { signature = update.Signature, url } },
            });
        });
        terminal.MapGet("/files/{file}", (string file, ITerminalUpdateFiles files) =>
            files.PathOf(file) is { } path ? Results.File(path, "application/octet-stream", file, enableRangeProcessing: true) : Results.NotFound());
    }
}
