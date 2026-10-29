using Tausi.Application.Setup;
using Tausi.Api.Platform;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Tausi.Application.Common;
using Tausi.Application.Menu;

namespace Tausi.Api.Endpoints;

public static class MenuEndpoints
{
    /// <summary>
    /// Menu admin (/api/admin/menu) and the terminal menu (/api/pos/menu). Structure endpoints
    /// refuse price fields; prices only go through the /prices endpoints (menu.edit_prices).
    /// </summary>
    public static void MapMenuEndpoints(this WebApplication app)
    {
        var menu = app.MapGroup("/api/admin/menu").WithTags("Menu").RequireAuthorization().RequiresLicence(LicensedFeature.MenuAdmin);

        menu.MapGet("/", (GetAdminMenu get, CancellationToken ct) => get.Execute(ct));
        menu.MapGet("/price-log", (Guid? itemId, int? take, GetPriceLog log, CancellationToken ct) =>
            log.Execute(itemId, take ?? 100, ct));

        menu.MapPost("/categories", (CategoryRequest r, SaveCategory save, CancellationToken ct) => save.Execute(null, r, ct));
        menu.MapPut("/categories/{id:guid}", (Guid id, CategoryRequest r, SaveCategory save, CancellationToken ct) => save.Execute(id, r, ct));
        menu.MapPost("/categories/reorder", async (ReorderRequest r, ReorderCategories reorder, CancellationToken ct) =>
        {
            await reorder.Execute(r, ct);
            return Results.NoContent();
        });
        menu.MapPost("/categories/{id:guid}/deactivate", async (Guid id, DeactivateCategoryRequest r, DeactivateCategory deactivate, CancellationToken ct) =>
            new { itemsDeactivated = await deactivate.Execute(id, r, ct) });

        menu.MapPost("/items", (ItemRequest r, SaveItem save, CancellationToken ct) => save.Execute(null, r, ct));
        menu.MapPut("/items/{id:guid}", (Guid id, ItemRequest r, SaveItem save, CancellationToken ct) => save.Execute(id, r, ct));
        menu.MapPost("/items/{id:guid}/image", async (Guid id, IFormFile file, ItemImages images, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return await images.Upload(id, new ItemImageUpload(file.FileName, file.ContentType, file.Length, stream), ct);
        }).DisableAntiforgery();
        menu.MapDelete("/items/{id:guid}/image", (Guid id, ItemImages images, CancellationToken ct) => images.Remove(id, ct));
        menu.MapPut("/items/{id:guid}/prices", async (Guid id, ItemPricesRequest r, SetItemPrices set, CancellationToken ct) =>
            new { changed = await set.Execute(id, r, ct) });

        menu.MapPost("/groups", (GroupRequest r, SaveGroup save, CancellationToken ct) => save.Execute(null, r, ct));
        menu.MapPut("/groups/{id:guid}", (Guid id, GroupRequest r, SaveGroup save, CancellationToken ct) => save.Execute(id, r, ct));
        menu.MapPut("/groups/{id:guid}/prices", async (Guid id, OptionPricesRequest r, SetOptionPrices set, CancellationToken ct) =>
            new { changed = await set.Execute(id, r, ct) });

        menu.MapPost("/price-lists", (PriceListRequest r, SavePriceList save, CancellationToken ct) => save.Execute(null, r, ct));
        menu.MapPut("/price-lists/{id:guid}", (Guid id, PriceListRequest r, SavePriceList save, CancellationToken ct) => save.Execute(id, r, ct));
        menu.MapPost("/price-lists/{id:guid}/make-default", async (Guid id, MakeDefaultPriceList make, CancellationToken ct) =>
        {
            await make.Execute(id, ct);
            return Results.NoContent();
        });

        menu.MapGet("/export.csv", async (ExportMenu export, CancellationToken ct) =>
            // With a byte order mark, so spreadsheet programs open it as UTF-8.
            Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(await export.Execute(ct))).ToArray(),
                "text/csv; charset=utf-8", "menu.csv"));

        menu.MapPost("/import/preview", async (IFormFile file, [FromForm] string? options, MenuImport import, CancellationToken ct) =>
            await import.Preview(await Read(file, ct), Options(options), ct)).DisableAntiforgery();
        menu.MapPost("/import/apply", async (IFormFile file, [FromForm] string? options, MenuImport import, CancellationToken ct) =>
            await import.Apply(await Read(file, ct), Options(options), ct)).DisableAntiforgery();
        menu.MapPost("/import/skipped", async (IFormFile file, [FromForm] string? options, MenuImport import, CancellationToken ct) =>
            Results.File(await import.SkippedRows(await Read(file, ct), Options(options), ct), "text/csv; charset=utf-8", "skipped-rows.csv"))
            .DisableAntiforgery().ReadsOnly();
        menu.MapPost("/import/sambapos/test", async (SambaPosConnection r, SambaPosImport import, CancellationToken ct) =>
            new { message = await import.Test(r, ct) });
        menu.MapPost("/import/sambapos/preview", (SambaImportRequest r, SambaPosImport import, CancellationToken ct) => import.Preview(r, ct));
        menu.MapPost("/import/sambapos/apply", (SambaImportRequest r, SambaPosImport import, CancellationToken ct) => import.Apply(r, ct))
            .RedactOnReplay("people", "pinSheetPdf");
        menu.MapGet("/import/template", () => Results.File(MenuImport.Template(), "text/csv; charset=utf-8", "tausi-menu-template.csv"));

        app.MapGet("/api/pos/menu", (GetPosMenu get, CancellationToken ct) => get.Execute(ct)).WithTags("Menu");

    }

    private const long MaxImportBytes = 5 * 1024 * 1024;

    private static async Task<byte[]> Read(IFormFile file, CancellationToken ct)
    {
        if (file.Length is 0 or > MaxImportBytes) throw new Domain.Common.DomainException("Choose a CSV or Excel file smaller than 5 MB.");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static ImportOptions Options(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new ImportOptions(null) : JsonSerializer.Deserialize<ImportOptions>(json, Json.Options)!;
}
