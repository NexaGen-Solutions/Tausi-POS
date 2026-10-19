using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexagen.Application.Abstractions;
using Nexagen.Application.Events;
using Nexagen.Application.Printing;
using Nexagen.Domain.Devices;
using Nexagen.Domain.Printing;

namespace Nexagen.Infrastructure.Printing;

/// <summary>
/// Sends due print jobs. Jobs for one printer go strictly in the order they were queued (the bill
/// before the dispense slip), so a job waiting to retry holds back later jobs for that printer.
/// </summary>
public sealed class PrintJobProcessor(
    INexagenDb db,
    IEnumerable<IPrinterTransport> transports,
    IOptions<NexagenOptions> options,
    IPosEvents events,
    TimeProvider clock,
    ILogger<PrintJobProcessor> log)
{
    /// <summary>Sends at most one job per printer. Returns how many jobs were tried.</summary>
    public async Task<int> ProcessDue(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // The queue is short (jobs leave it within seconds), so finding each printer's oldest job
        // in memory is cheap and works the same on every database.
        var queued = await db.PrintJobs.Include(j => j.Printer)
            .Where(j => j.Status == PrintJobStatus.Queued)
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id)
            .ToListAsync(ct);
        var jobs = queued.GroupBy(j => j.PrinterId).Select(g => g.First()).ToList();

        var tried = 0;
        foreach (var job in jobs.Where(j => j.NextAttemptAt <= now).OrderBy(j => j.CreatedAt))
        {
            tried++;
            await Send(job, ct);
        }
        return tried;
    }

    private async Task Send(PrintJob job, CancellationToken ct)
    {
        var printer = job.Printer!;
        var oldStatus = printer.Status;
        try
        {
            await TransportFor(printer).Send(printer, job, ct);
            job.MarkSent(clock.GetUtcNow());
            printer.Status = PrinterStatus.Online;
            printer.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = ex is PrinterTransportException ? ex.Message : $"Printing failed: {ex.Message}";
            var gaveUp = job.MarkAttemptFailed(error, clock.GetUtcNow());
            printer.Status = PrinterStatus.Error;
            printer.LastError = error;
            log.LogWarning("Print job {JobId} to {Printer} failed (try {Attempt}): {Error}", job.Id, printer.Name, job.Attempts, error);

            if (gaveUp)
            {
                await db.SaveChangesAsync(ct);
                await events.Publish(PosEventNames.PrintFailed, new
                {
                    printJobId = job.Id,
                    printerId = printer.Id,
                    printerName = printer.Name,
                    kind = job.Kind.ToString(),
                    ticketId = job.TicketId,
                }, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        if (printer.Status != oldStatus)
            await events.Publish(PosEventNames.PrinterStatus, new { printerId = printer.Id, status = printer.Status.ToString() }, ct);
    }

    private IPrinterTransport TransportFor(Printer printer)
    {
        var wanted = options.Value.Printing.Transport == PrintTransportMode.File ? null : (PrinterConnection?)printer.ConnectionMode;
        return transports.FirstOrDefault(t => t.Mode == wanted)
            ?? throw new PrinterTransportException($"No transport for {printer.ConnectionMode}.");
    }
}
