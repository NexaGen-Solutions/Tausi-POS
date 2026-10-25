using Tausi.Application.Setup;
using Tausi.Api.Platform;
using Tausi.Application.Stock;

namespace Tausi.Api.Endpoints;

public static class StockEndpoints
{
    public static void MapStockEndpoints(this WebApplication app)
    {
        var stock = app.MapGroup("/api/admin/stock").WithTags("Stock").RequireAuthorization().RequiresLicence(LicensedFeature.StockAdmin);
        stock.MapGet("/", (Guid? locationId, StockCatalog catalog, CancellationToken ct) => catalog.Overview(locationId, ct));
        stock.MapGet("/locations", (StockCatalog catalog, CancellationToken ct) => catalog.Locations(ct));
        stock.MapGet("/locations/all", (StockLocations locations, CancellationToken ct) => locations.List(ct));
        stock.MapPost("/locations", (SaveLocationRequest r, StockLocations locations, CancellationToken ct) => locations.Save(null, r, ct));
        stock.MapPut("/locations/{id:guid}", (Guid id, SaveLocationRequest r, StockLocations locations, CancellationToken ct) => locations.Save(id, r, ct));
        stock.MapGet("/from-menu", (StockFromMenu helper, CancellationToken ct) => helper.Suggestions(ct));
        stock.MapPost("/from-menu", async (CreateFromMenuRequest r, StockFromMenu helper, CancellationToken ct) => new { created = await helper.Create(r, ct) });
        stock.MapGet("/items/{id:guid}/movements", (Guid id, DateOnly? from, DateOnly? to, StockCatalog catalog, CancellationToken ct) =>
            catalog.History(id, from, to, ct));
        stock.MapPost("/items", async (SaveStockItemRequest r, StockItemCommands items, CancellationToken ct) =>
            new { id = await items.Save(null, r, ct) });
        stock.MapPut("/items/{id:guid}", async (Guid id, SaveStockItemRequest r, StockItemCommands items, CancellationToken ct) =>
            new { id = await items.Save(id, r, ct) });

        stock.MapGet("/recipes", (RecipeCommands recipes, CancellationToken ct) => recipes.MenuItems(ct));
        stock.MapGet("/recipes/{menuItemId:guid}", (Guid menuItemId, RecipeCommands recipes, CancellationToken ct) => recipes.Get(menuItemId, ct));
        stock.MapPut("/recipes/{menuItemId:guid}", (Guid menuItemId, SaveRecipeRequest r, RecipeCommands recipes, CancellationToken ct) =>
            recipes.Save(menuItemId, r, ct));

        stock.MapPost("/waste", (RecordWasteRequest r, WasteAndTransferCommands c, CancellationToken ct) => c.RecordWaste(r, ct));
        stock.MapPost("/transfers", (TransferRequest r, WasteAndTransferCommands c, CancellationToken ct) => c.RecordTransfer(r, ct));
        stock.MapGet("/activity", (string? kind, DateOnly? from, WasteAndTransferCommands c, CancellationToken ct) => c.Activity(kind, from, null, ct));
        stock.MapPost("/activity/{id:guid}/approve", (Guid id, WasteAndTransferCommands c, CancellationToken ct) => c.Approve(id, ct));
        stock.MapPost("/activity/{id:guid}/reject", (Guid id, RejectStockRequest r, WasteAndTransferCommands c, CancellationToken ct) => c.Reject(id, r, ct));
        stock.MapGet("/awaiting", (AwaitingApproval awaiting, CancellationToken ct) => awaiting.Execute(ct));

        var suppliers = app.MapGroup("/api/admin/suppliers").WithTags("Stock").RequireAuthorization().RequiresLicence(LicensedFeature.StockAdmin);
        suppliers.MapGet("/", (SupplierCommands s, CancellationToken ct) => s.List(ct));
        suppliers.MapPost("/", (SaveSupplierRequest r, SupplierCommands s, CancellationToken ct) => s.Save(null, r, ct));
        suppliers.MapPut("/{id:guid}", (Guid id, SaveSupplierRequest r, SupplierCommands s, CancellationToken ct) => s.Save(id, r, ct));

        var deliveries = app.MapGroup("/api/admin/deliveries").WithTags("Stock").RequireAuthorization().RequiresLicence(LicensedFeature.StockAdmin);
        deliveries.MapGet("/", (DeliveryCommands d, CancellationToken ct) => d.List(ct));
        deliveries.MapGet("/{id:guid}", (Guid id, DeliveryCommands d, CancellationToken ct) => d.Get(id, ct));
        deliveries.MapGet("/last-cost", (Guid supplierId, Guid stockItemId, string? unit, DeliveryCommands d, CancellationToken ct) =>
            d.LastCost(supplierId, stockItemId, unit, ct));
        deliveries.MapPost("/", (ReceiveDeliveryRequest r, DeliveryCommands d, CancellationToken ct) => d.Receive(r, ct));
        deliveries.MapPost("/{id:guid}/approve", (Guid id, DeliveryCommands d, CancellationToken ct) => d.Approve(id, ct));
        deliveries.MapPost("/{id:guid}/reject", (Guid id, RejectStockRequest r, DeliveryCommands d, CancellationToken ct) => d.Reject(id, r, ct));
        deliveries.MapPost("/{id:guid}/reverse", (Guid id, ReverseDeliveryRequest r, DeliveryCommands d, CancellationToken ct) => d.Reverse(id, r, ct));
        deliveries.MapPost("/{id:guid}/reverse/approve", (Guid id, DeliveryCommands d, CancellationToken ct) => d.ApproveReversal(id, ct));

        var counts = app.MapGroup("/api/admin/stock-counts").WithTags("Stock").RequireAuthorization().RequiresLicence(LicensedFeature.StockAdmin);
        counts.MapGet("/", (StockCountCommands c, CancellationToken ct) => c.List(ct));
        counts.MapGet("/{id:guid}", (Guid id, StockCountCommands c, CancellationToken ct) => c.Get(id, ct));
        counts.MapPost("/", (StartCountRequest r, StockCountCommands c, CancellationToken ct) => c.Start(r, ct));
        counts.MapPut("/{id:guid}/lines", (Guid id, EnterCountsRequest r, StockCountCommands c, CancellationToken ct) => c.Enter(id, r, ct));
        counts.MapPost("/{id:guid}/submit", (Guid id, StockCountCommands c, CancellationToken ct) => c.Submit(id, ct));
        counts.MapPost("/{id:guid}/approve", (Guid id, StockCountCommands c, CancellationToken ct) => c.Approve(id, ct));
        counts.MapPost("/{id:guid}/reject", (Guid id, RejectStockRequest r, StockCountCommands c, CancellationToken ct) => c.Reject(id, r, ct));
        counts.MapGet("/{id:guid}/sheet.pdf", async (Guid id, StockCountCommands c, CancellationToken ct) =>
            Results.File(await c.SheetPdf(id, ct), "application/pdf", "count-sheet.pdf"));
        counts.MapPost("/{id:guid}/print", async (Guid id, PrintCountSheetRequest r, StockCountCommands c, CancellationToken ct) =>
        {
            await c.PrintSheet(id, r, ct);
            return Results.NoContent();
        });
    }
}
