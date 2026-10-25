using Tausi.Application.Setup;
using Tausi.Api.Platform;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Files;
using Tausi.Application.Settings;
using Tausi.Infrastructure.Persistence;

namespace Tausi.Api.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        // Business name and logo, shown before anyone logs in.
        app.MapGet("/api/brand", (GetBrand brand, CancellationToken ct) => brand.Execute(ct))
            .WithTags("Settings").RequireCors(CorsSetup.TerminalApp);

        var settings = app.MapGroup("/api/admin/settings").WithTags("Settings").RequireAuthorization().RequiresLicence(LicensedFeature.Settings);

        settings.MapGet("/", (GetSettings get, CancellationToken ct) => get.Execute(ct));
        settings.MapPut("/", (UpdateSettingsRequest request, UpdateSettings update, CancellationToken ct) => update.Execute(request, ct));
        settings.MapGet("/history", (SettingsHistory history, CancellationToken ct) => history.Execute(ct));

        settings.MapPost("/logo", async (IFormFile file, LogoCommands logo, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await logo.Upload(new LogoUpload(file.FileName, file.ContentType, file.Length, stream), ct);
        }).DisableAntiforgery();
        settings.MapDelete("/logo", (LogoCommands logo, CancellationToken ct) => logo.Remove(ct));
        settings.MapGet("/logo/print.png", async (int? paper, LogoPrintPreview preview, CancellationToken ct) =>
            await preview.Execute(paper ?? 80, ct) is { } png ? Results.File(png, "image/png") : Results.NotFound());

        // The sample bill and receipt with the saved settings, or with unsaved ones: as JSON in the
        // body (POST), or in the query string (?width=42&values={"business.name":"..."}).
        settings.MapPost("/receipt-preview", (ReceiptPreviewRequest request, ReceiptPreview preview, CancellationToken ct) =>
            preview.Execute(request, ct));
        settings.MapGet("/receipt-preview", (int? width, string? values, ReceiptPreview preview, CancellationToken ct) =>
            preview.Execute(new ReceiptPreviewRequest(width ?? 42, ParseValues(values)), ct));

        app.MapGet("/files/{id:guid}", async (Guid id, TausiDbContext db, IFileStorage files, CancellationToken ct) =>
        {
            // Private files (expense receipts) are served only through their own checked endpoint.
            var meta = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && !f.IsPrivate, ct);
            var stream = meta is null ? null : files.Open(id);
            return stream is null
                ? Results.NotFound()
                : Results.File(stream, meta!.ContentType, enableRangeProcessing: false);
        }).WithTags("Settings");
    }

    private static Dictionary<string, System.Text.Json.JsonElement>? ParseValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new Domain.Common.DomainException("The preview values must be a JSON object of setting keys.");
        }
    }
}
