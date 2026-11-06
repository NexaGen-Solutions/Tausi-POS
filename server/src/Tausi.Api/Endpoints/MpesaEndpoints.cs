using Tausi.Api.Platform;
using Tausi.Application.Mpesa;
using Tausi.Application.Setup;

namespace Tausi.Api.Endpoints;

public static class MpesaEndpoints
{
    public static void MapMpesaEndpoints(this WebApplication app)
    {
        // The terminal: what's set up, prompts (settle and order screens), and Payments received.
        var pos = app.MapGroup("/api/pos/mpesa").WithTags("M-Pesa").RequireAuthorization();
        pos.MapGet("/", (MpesaAdmin admin, MpesaReceived received, CancellationToken ct) => admin.ForPos(received, ct));
        pos.MapPost("/tickets/{id:guid}/prompts", (Guid id, SendPromptRequest r, MpesaPrompts prompts, CancellationToken ct) => prompts.Send(id, r, ct));
        pos.MapGet("/tickets/{id:guid}/prompts/current", async (Guid id, MpesaPrompts prompts, CancellationToken ct) =>
            await prompts.Current(id, ct) is { } prompt ? Results.Ok(prompt) : Results.NoContent());
        // Asking about a prompt may ask Daraja and record its answer: a read for the client (no Idempotency-Key).
        pos.MapPost("/prompts/{id:guid}/status", (Guid id, MpesaPrompts prompts, CancellationToken ct) => prompts.Status(id, ct)).ReadsOnly();
        pos.MapPost("/prompts/{id:guid}/cancel", (Guid id, MpesaPrompts prompts, CancellationToken ct) => prompts.Cancel(id, ct));
        pos.MapGet("/tickets/{id:guid}/received", (Guid id, MpesaReceived received, CancellationToken ct) => received.For(id, ct));
        // The read-only M-Pesa list on the terminal (reports.all or cash.book).
        pos.MapPost("/ledger", (MpesaLedgerFilters filters, MpesaLedger ledger, CancellationToken ct) => ledger.List(filters, ct)).ReadsOnly();

        // The admin site: Settings → M-Pesa (settings.manage) and the M-Pesa payments ledger.
        var admin = app.MapGroup("/api/admin/mpesa").WithTags("M-Pesa").RequireAuthorization();
        admin.MapGet("/", (MpesaAdmin mpesa, CancellationToken ct) => mpesa.Get(ct)).RequiresLicence(LicensedFeature.Settings);
        admin.MapPut("/secrets", (SaveMpesaSecretsRequest r, MpesaAdmin mpesa, CancellationToken ct) => mpesa.SaveSecrets(r, ct))
            .RequiresLicence(LicensedFeature.Settings);
        admin.MapPost("/test", (MpesaAdmin mpesa, CancellationToken ct) => mpesa.Test(ct)).RequiresLicence(LicensedFeature.Settings);

        admin.MapPost("/ledger", (MpesaLedgerFilters filters, MpesaLedger ledger, CancellationToken ct) => ledger.List(filters, ct)).ReadsOnly();
        admin.MapPost("/ledger/export", async (MpesaLedgerFilters filters, MpesaLedger ledger, CancellationToken ct) =>
        {
            var file = await ledger.Export(filters, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        }).ReadsOnly();
        admin.MapGet("/ledger/open-tickets", (MpesaLedger ledger, CancellationToken ct) => ledger.OpenTickets(ct));
        admin.MapPost("/ledger/{id:guid}/apply", (Guid id, ApplyToTicketRequest r, MpesaLedger ledger, CancellationToken ct) => ledger.ApplyToTicket(id, r, ct));
        admin.MapPost("/ledger/{id:guid}/not-from-pos", (Guid id, NotFromPosRequest r, MpesaLedger ledger, CancellationToken ct) =>
            ledger.MarkNotFromPos(id, r, ct));
        admin.MapPost("/fetch", async (MpesaLedger ledger, CancellationToken ct) => Results.Ok(new { saved = await ledger.FetchNow(ct) }));
    }
}
