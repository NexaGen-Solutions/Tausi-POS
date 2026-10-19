using Microsoft.EntityFrameworkCore;
using Nexagen.Application.Files;
using Nexagen.Application.Settings;
using Nexagen.Infrastructure.Persistence;

namespace Nexagen.Api.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        // Business name and logo, shown before anyone logs in.
        app.MapGet("/api/brand", (GetBrand brand, CancellationToken ct) => brand.Execute(ct))
            .WithTags("Settings").RequireCors(CorsSetup.TerminalApp);

        var settings = app.MapGroup("/api/settings").WithTags("Settings").RequireAuthorization();

        settings.MapGet("/business", (GetBusinessSettings get, CancellationToken ct) => get.Execute(ct));

        settings.MapPut("/business", (UpdateBusinessSettingsRequest request, UpdateBusinessSettings update, CancellationToken ct) =>
            update.Execute(request, ct));

        settings.MapPost("/business/logo", async (IFormFile file, UploadBusinessLogo upload, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await upload.Execute(new LogoUpload(file.FileName, file.ContentType, file.Length, stream), ct);
        }).DisableAntiforgery();

        app.MapGet("/files/{id:guid}", async (Guid id, NexagenDbContext db, IFileStorage files, CancellationToken ct) =>
        {
            var meta = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
            var stream = meta is null ? null : files.Open(id);
            return stream is null
                ? Results.NotFound()
                : Results.File(stream, meta!.ContentType, enableRangeProcessing: false);
        }).WithTags("Settings");
    }
}
