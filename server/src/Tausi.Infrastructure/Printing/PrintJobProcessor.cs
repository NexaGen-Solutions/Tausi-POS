using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tausi.Application.Abstractions;
using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Domain.Devices;
using Tausi.Domain.Printing;

namespace Tausi.Infrastructure.Printing;

/// <summary>
/// Sends due print jobs. Jobs for one printer go strictly in the order they were queued (the bill
/// before the dispense slip), so a job waiting to retry holds back later jobs for that printer.
/// </summary>
public sealed class PrintJobProcessor(
    ITausiDb db,
    IEnumerable<IPrinterTransport> transports,
    IOptions<TausiOptions> options,
    IPosEvents events,
    TimeProvider clock,
    ILogger<PrintJobProcessor> log)
{
    /// <summary>
    /// Sends at most one job per printer. A ticket's slips also keep their order across printers:
    /// a job waits while an earlier job of the same ticket has failed or is waiting to retry, so a
    /// bill always prints before its dispense slip, even after a retry. Returns how many were tried.
    /// </summary>
    public async Task<int> ProcessDue(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // The queue is short (jobs leave it within seconds), so finding each printer's oldest job
        // in memory is cheap and works the same on every database.
        var queued = await db.PrintJobs.Include(j => j.Printer)
            .Where(j => j.Status == PrintJobStatus.Queued)
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id)
            .ToListAsync(ct);
        if (queued.Count == 0) return 0;

        var tickets = queued.Where(j => j.TicketId != null).Select(j => j.TicketId).Distinct().ToList();
        var failed = tickets.Count == 0
            ? []
            : await db.PrintJobs.AsNoTracking()
                .Where(j => j.Status == PrintJobStatus.Failed && tickets.Contains(j.TicketId))
                .Select(j => new { j.TicketId, j.CreatedAt })
                .ToListAsync(ct);

        bool WaitsForEarlier(PrintJob job) =>
            job.TicketId is { } ticket
            && (failed.Any(f => f.TicketId == ticket && f.CreatedAt < job.CreatedAt)
                || queued.Any(q => q.TicketId == ticket && q.Id != job.Id
                    && q.Status is PrintJobStatus.Queued or PrintJobStatus.Failed
                    && (q.CreatedAt < job.CreatedAt || (q.CreatedAt == job.CreatedAt && q.Id.CompareTo(job.Id) < 0))));

        var heads = queued.GroupBy(j => j.PrinterId).Select(g => g.First()).OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).ToList();
        var tried = 0;
        foreach (var job in heads)
        {
            if (job.NextAttemptAt > now || WaitsForEarlier(job)) continue;
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
            await TransportFor(printer).Send(printer, WithDrawerPulse(printer, job), ct);
            job.MarkSent(clock.GetUtcNow());
            printer.Status = PrinterStatus.Online;
            printer.LastError = null;
            printer.LastPrintedAt = job.SentAt;
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

    /// <summary>
    /// A final receipt on a printer set to open the cash drawer gets the drawer pulse at the end.
    /// Nothing else ever opens the drawer.
    /// </summary>
    public static PrintJob WithDrawerPulse(Printer printer, PrintJob job)
    {
        if (job.Kind != PrintJobKind.Receipt || !printer.OpenDrawerOnReceipt) return job;
        return new PrintJob
        {
            Id = job.Id, PrinterId = job.PrinterId, Kind = job.Kind, TicketId = job.TicketId,
            Content = [.. job.Content, .. ReceiptBuilder.DrawerPulse],
            TextPreview = job.TextPreview + "[open cash drawer]" + Environment.NewLine,
        };
    }

    private IPrinterTransport TransportFor(Printer printer)
    {
        var wanted = options.Value.Printing.Transport == PrintTransportMode.File ? PrinterConnection.File : printer.ConnectionMode;
        return transports.FirstOrDefault(t => t.Mode == wanted)
            ?? throw new PrinterTransportException($"No transport for {printer.ConnectionMode}.");
    }
}
