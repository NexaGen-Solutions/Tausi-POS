using Tausi.Application.Approvals;
using Tausi.Application.Day;
using Tausi.Application.Settling;
using Tausi.Application.Tables;
using Tausi.Domain.Service;
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

        // The order screen: every change is saved as it happens and needs this terminal's edit lock.
        pos.MapPatch("/tickets/{id:guid}", (Guid id, UpdateTicketRequest r, UpdateTicket update, CancellationToken ct) =>
            update.Execute(id, r, ct));
        pos.MapPost("/tickets/{id:guid}/lines", (Guid id, AddLineRequest r, AddLine add, CancellationToken ct) => add.Execute(id, r, ct));
        pos.MapPatch("/tickets/{id:guid}/lines/{lineId:guid}", (Guid id, Guid lineId, ChangeLineRequest r, ChangeLine change, CancellationToken ct) =>
            change.Quantity(id, lineId, r, ct));
        pos.MapDelete("/tickets/{id:guid}/lines/{lineId:guid}", (Guid id, Guid lineId, ChangeLine change, CancellationToken ct) =>
            change.Remove(id, lineId, ct));
        pos.MapPost("/tickets/{id:guid}/print", (Guid id, PrintTicket print, CancellationToken ct) => print.Print(id, ct));
        pos.MapPost("/tickets/{id:guid}/send", (Guid id, PrintTicket print, CancellationToken ct) => print.Send(id, ct));
        pos.MapPost("/tickets/{id:guid}/print-bill", (Guid id, PrintTicket print, CancellationToken ct) => print.PrintBill(id, ct));
        pos.MapPost("/tickets/{id:guid}/reprint-bill", (Guid id, PrintTicket print, CancellationToken ct) => print.Reprint(id, ct));
        // Void, gift, price change and discount, done now (Allowed, or with a supervisor's PIN token).
        pos.MapPost("/tickets/{id:guid}/lines/{lineId:guid}/void", (Guid id, Guid lineId, VoidLineRequest r, TicketActionCommands act, CancellationToken ct) =>
            act.Void(id, lineId, r, ct));
        pos.MapPost("/tickets/{id:guid}/lines/{lineId:guid}/gift", (Guid id, Guid lineId, GiftLineRequest r, TicketActionCommands act, CancellationToken ct) =>
            act.Gift(id, lineId, r, ct));
        pos.MapPost("/tickets/{id:guid}/lines/{lineId:guid}/price", (Guid id, Guid lineId, ChangePriceRequest r, TicketActionCommands act, CancellationToken ct) =>
            act.ChangePrice(id, lineId, r, ct));
        pos.MapPost("/tickets/{id:guid}/discount", (Guid id, DiscountRequest r, TicketActionCommands act, CancellationToken ct) =>
            act.Discount(id, r, ct));

        // Approval requests ("Option 2 · supervisor elsewhere") and the approvals screen.
        pos.MapPost("/approvals", (CreateApprovalRequestDto r, CreateApprovalRequest create, CancellationToken ct) => create.Execute(r, ct));
        pos.MapGet("/approvals", (string? status, ListApprovals list, CancellationToken ct) => list.Execute(status, ct));
        pos.MapGet("/approvals/approvers", (ApprovalKind kind, Approvers approvers, CancellationToken ct) => approvers.Execute(kind, ct));
        // The body is optional: the approver's answer to "Was it already poured?" for a void.
        pos.MapPost("/approvals/{id:guid}/approve", async (Guid id, HttpRequest http, ApproveRequest approve, CancellationToken ct) =>
            await approve.Execute(id, ct, http.ContentLength is > 0 ? await http.ReadFromJsonAsync<ApproveRequestDto>(ct) : null));
        pos.MapPost("/approvals/{id:guid}/reject", (Guid id, RejectApprovalRequestDto? r, RejectRequest reject, CancellationToken ct) =>
            reject.Execute(id, r ?? new RejectApprovalRequestDto(null), ct));
        pos.MapPost("/approvals/{id:guid}/cancel", (Guid id, CancelRequest cancel, CancellationToken ct) => cancel.Execute(id, ct));

        // Settling (cashier only): payments saved as they're added, then complete.
        pos.MapGet("/tickets/settled", (GetSettledTickets list, CancellationToken ct) => list.Execute(ct));
        pos.MapGet("/tickets/{id:guid}/brought-by", (Guid id, BroughtByChoices choices, CancellationToken ct) => choices.Execute(id, ct));
        pos.MapPost("/tickets/{id:guid}/payments", (Guid id, AddPaymentRequest r, PaymentCommands payments, CancellationToken ct) =>
            payments.Add(id, r, ct));
        pos.MapDelete("/tickets/{id:guid}/payments/{paymentId:guid}", (Guid id, Guid paymentId, PaymentCommands payments, CancellationToken ct) =>
            payments.Remove(id, paymentId, ct));
        pos.MapPost("/tickets/{id:guid}/settle", (Guid id, SettleTicket settle, CancellationToken ct) => settle.Execute(id, ct));
        pos.MapPost("/tickets/{id:guid}/receipt/reprint", async (Guid id, ReprintReceipt reprint, CancellationToken ct) =>
        {
            await reprint.Execute(id, ct);
            return Results.NoContent();
        });
        pos.MapPost("/tickets/{id:guid}/close-voided", async (Guid id, CloseVoidedTicket close, CancellationToken ct) =>
        {
            await close.Execute(id, ct);
            return Results.NoContent();
        });
        pos.MapPost("/tickets/{id:guid}/close-empty", async (Guid id, CloseEmptyTicket close, CancellationToken ct) =>
        {
            await close.Execute(id, ct);
            return Results.NoContent();
        });

        pos.MapPost("/tickets/{id:guid}/print-jobs/{jobId:guid}/retry", async (Guid id, Guid jobId, RetryTicketPrint retry, CancellationToken ct) =>
        {
            await retry.Execute(id, jobId, ct);
            return Results.NoContent();
        });

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
