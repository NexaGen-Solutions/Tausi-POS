using Tausi.Application.Printers;

namespace Tausi.Api.Endpoints;

public static class PrinterEndpoints
{
    public static void MapPrinterEndpoints(this WebApplication app)
    {
        var printers = app.MapGroup("/api/printers").WithTags("Printers").RequireAuthorization();

        printers.MapPost("/{id:guid}/test", (Guid id, TestPrint test, CancellationToken ct) => test.Execute(id, ct));
    }
}
