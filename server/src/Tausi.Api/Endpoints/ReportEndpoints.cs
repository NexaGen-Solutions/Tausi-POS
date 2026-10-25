using Tausi.Application.Setup;
using Tausi.Api.Platform;
using Tausi.Application.Reports;

namespace Tausi.Api.Endpoints;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this WebApplication app)
    {
        var reports = app.MapGroup("/api/admin/reports").WithTags("Reports").RequireAuthorization().RequiresLicence(LicensedFeature.Reports);
        reports.MapGet("/", (ReportService r, CancellationToken ct) => r.List(ct));
        reports.MapGet("/options", (ReportService r, CancellationToken ct) => r.Options(ct));
        // Running and exporting only read: POST carries the filters.
        reports.MapPost("/{key}/run", (string key, ReportFilters filters, ReportService r, CancellationToken ct) => r.Run(key, filters, ct)).ReadsOnly();
        reports.MapPost("/{key}/export", async (string key, string? format, ReportFilters filters, ReportService r, CancellationToken ct) =>
        {
            var file = await r.Export(key, filters, format ?? "xlsx", ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
        reports.MapPost("/{key}/print", async (string key, string? target, Guid? printerId, ReportFilters filters, ReportService r, CancellationToken ct) =>
        {
            if (target == "printer")
            {
                if (printerId is not { } printer) return Results.BadRequest(new { detail = "Choose a printer." });
                await r.PrintReceipt(key, filters, printer, ct);
                return Results.NoContent();
            }
            var file = await r.Pdf(key, filters, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });

        var tickets = app.MapGroup("/api/admin/tickets").WithTags("Reports").RequireAuthorization().RequiresLicence(LicensedFeature.Reports);
        tickets.MapGet("/{id:guid}", (Guid id, TicketDetailReport detail, CancellationToken ct) => detail.Execute(id, ct));
        tickets.MapGet("/{id:guid}/pdf", async (Guid id, TicketDetailReport detail, CancellationToken ct) =>
        {
            var file = await detail.Pdf(id, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });

        // The supervisors' Sales by user slip from the Close day screen.
        app.MapPost("/api/pos/reports/sales-by-user/print", async (ReportService r, CancellationToken ct) =>
        {
            await r.PrintSalesByUserHere(ct);
            return Results.NoContent();
        }).WithTags("Reports").RequireAuthorization();
    }
}
