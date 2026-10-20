using Tausi.Application.Printers;
using Tausi.Domain.Printing;

namespace Tausi.Api.Endpoints;

public static class PrinterEndpoints
{
    public static void MapPrinterEndpoints(this WebApplication app)
    {
        var printers = app.MapGroup("/api/admin/printers").WithTags("Printers").RequireAuthorization();

        printers.MapGet("/", (ListPrinters list, CancellationToken ct) => list.Execute(ct));
        printers.MapPost("/", (PrinterRequest r, SavePrinter save, CancellationToken ct) => save.Execute(null, r, ct));
        printers.MapPut("/{id:guid}", (Guid id, PrinterRequest r, SavePrinter save, CancellationToken ct) => save.Execute(id, r, ct));
        printers.MapPost("/check", (CheckPrinterRequest r, CheckPrinter check, CancellationToken ct) => check.Execute(r, ct));
        printers.MapPost("/{id:guid}/test", (Guid id, TestPrint test, CancellationToken ct) => test.Execute(id, ct));
        printers.MapGet("/{id:guid}/jobs", (Guid id, PrintJobStatus? status, ListPrinterJobs list, CancellationToken ct) =>
            list.Execute(id, status ?? PrintJobStatus.Failed, ct));
        printers.MapPost("/{id:guid}/jobs/retry-all", async (Guid id, PrintJobActions actions, CancellationToken ct) =>
            new { retried = await actions.RetryAll(id, ct) });

        var jobs = app.MapGroup("/api/admin/print-jobs").WithTags("Printers").RequireAuthorization();
        jobs.MapPost("/{id:guid}/retry", async (Guid id, PrintJobActions actions, CancellationToken ct) =>
        {
            await actions.Retry(id, ct);
            return Results.NoContent();
        });
        jobs.MapPost("/{id:guid}/redirect", async (Guid id, RedirectRequest r, PrintJobActions actions, CancellationToken ct) =>
        {
            await actions.Redirect(id, r, ct);
            return Results.NoContent();
        });
        jobs.MapPost("/{id:guid}/discard", async (Guid id, DiscardRequest r, PrintJobActions actions, CancellationToken ct) =>
        {
            await actions.Discard(id, r, ct);
            return Results.NoContent();
        });

        // Kept from the foundation for existing callers.
        app.MapPost("/api/printers/{id:guid}/test", (Guid id, TestPrint test, CancellationToken ct) => test.Execute(id, ct))
            .WithTags("Printers").RequireAuthorization();
    }
}
