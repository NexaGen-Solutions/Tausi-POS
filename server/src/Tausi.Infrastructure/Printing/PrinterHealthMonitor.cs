using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tausi.Application.Abstractions;
using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Domain.Devices;

namespace Tausi.Infrastructure.Printing;

/// <summary>Every 60 seconds, checks that active network printers answer, and publishes any change.</summary>
public sealed class PrinterHealthMonitor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PrinterHealthMonitor> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await CheckAll(scope.ServiceProvider.GetRequiredService<ITausiDb>(), scope.ServiceProvider.GetRequiredService<IPosEvents>(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Printer check failed");
            }
        } while (await Tick(timer, stoppingToken));
    }

    public static async Task CheckAll(ITausiDb db, IPosEvents events, CancellationToken ct)
    {
        var printers = await db.Printers.Where(p => p.Active && p.ConnectionMode == PrinterConnection.Network9100).ToListAsync(ct);
        foreach (var printer in printers)
        {
            var (status, error) = (PrinterStatus.Online, (string?)null);
            try
            {
                await NetworkPrinter.Probe(printer.Address, PrinterChecker.NetworkTimeout, ct);
            }
            catch (PrinterTransportException ex)
            {
                (status, error) = (PrinterStatus.Error, ex.Message);
            }
            if (printer.Status == status && printer.LastError == error) continue;
            printer.Status = status;
            printer.LastError = error;
            await db.SaveChangesAsync(ct);
            await events.Publish(PosEventNames.PrinterStatus, new { printerId = printer.Id, status = status.ToString() }, ct);
        }
    }

    private static async Task<bool> Tick(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
