using Tausi.Application.Tables;
using Tausi.Application.Tickets;

namespace Tausi.Api.Endpoints;

public static class TicketEndpoints
{
    /// <summary>The table map and tickets for terminals (/api/pos), and the Tables admin page (/api/admin/tables).</summary>
    public static void MapTicketEndpoints(this WebApplication app)
    {
        var pos = app.MapGroup("/api/pos").WithTags("Tickets").RequireAuthorization();

        // ?tableIds=a,b returns just those tables, for patching the map after an event.
        pos.MapGet("/tables", (string? tableIds, GetTableMap map, CancellationToken ct) =>
            map.Execute(ParseIds(tableIds), ct));
        pos.MapGet("/tickets/takeaway", (GetTakeawayTickets list, CancellationToken ct) => list.Execute(ct));
        pos.MapGet("/tickets/{id:guid}", (Guid id, GetTicket get, CancellationToken ct) => get.Execute(id, ct));
        pos.MapPost("/tickets", (StartTicketRequest r, StartTicket start, CancellationToken ct) => start.Execute(r, ct));
        pos.MapPost("/tickets/{id:guid}/lock", (Guid id, LockTicket take, CancellationToken ct) => take.Execute(id, ct));
        pos.MapDelete("/tickets/{id:guid}/lock", async (Guid id, UnlockTicket release, CancellationToken ct) =>
        {
            await release.Execute(id, ct);
            return Results.NoContent();
        });
        pos.MapPost("/tickets/{id:guid}/move", (Guid id, MoveTicketRequest r, MoveTicket move, CancellationToken ct) =>
            move.Execute(id, r, ct));
        pos.MapPost("/tickets/merge", (MergeTicketsRequest r, MergeTickets merge, CancellationToken ct) => merge.Execute(r, ct));

        var admin = app.MapGroup("/api/admin/tables").WithTags("Tables").RequireAuthorization();
        admin.MapGet("/", (GetTablesAdmin get, CancellationToken ct) => get.Execute(ct));
        admin.MapPost("/", async (TableRequest r, SaveTable save, CancellationToken ct) =>
        {
            await save.Execute(null, r, ct);
            return Results.NoContent();
        });
        admin.MapPut("/{id:guid}", async (Guid id, TableRequest r, SaveTable save, CancellationToken ct) =>
        {
            await save.Execute(id, r, ct);
            return Results.NoContent();
        });
        admin.MapDelete("/{id:guid}", async (Guid id, DeleteTableOrArea delete, CancellationToken ct) =>
        {
            await delete.Table(id, ct);
            return Results.NoContent();
        });
        admin.MapPost("/quick-add", (QuickAddRequest r, QuickAddTables add, CancellationToken ct) => add.Execute(r, ct));
        admin.MapPost("/areas", (AreaRequest r, SaveArea save, CancellationToken ct) => save.Execute(null, r, ct));
        admin.MapPut("/areas/{id:guid}", (Guid id, AreaRequest r, SaveArea save, CancellationToken ct) => save.Execute(id, r, ct));
        admin.MapDelete("/areas/{id:guid}", async (Guid id, DeleteTableOrArea delete, CancellationToken ct) =>
        {
            await delete.Area(id, ct);
            return Results.NoContent();
        });
    }

    private static List<Guid>? ParseIds(string? ids) =>
        string.IsNullOrWhiteSpace(ids)
            ? null
            : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
}
