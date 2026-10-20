using Microsoft.AspNetCore.SignalR;
using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Application.Terminals;

namespace Tausi.Api.Hubs;

public sealed class SignalRPosEvents(IHubContext<PosHub> hub) : IPosEvents
{
    public Task Publish(string eventName, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(PosHub.BusinessGroup).SendAsync(eventName, payload, ct);
}

/// <summary>
/// Reaches the terminal app a printer is plugged into, and waits for its answer (a SignalR client
/// result): "print.job" prints through the app's Tauri command; "printer.check" checks the printer exists.
/// </summary>
public sealed class HubTerminalPrintChannel(IHubContext<PosHub> hub, TerminalPresence presence) : ITerminalPrintChannel
{
    public const string PrintMethod = "print.job";
    public const string CheckMethod = "printer.check";
    private static readonly TimeSpan PrintTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    public sealed record PrintResult(bool Ok, string? Error);

    public Task Print(Guid terminalId, string printerName, byte[] content, CancellationToken ct) =>
        Ask(terminalId, PrintMethod, new { printer = printerName, dataBase64 = Convert.ToBase64String(content) }, PrintTimeout,
            "The terminal did not confirm the print.", ct);

    public Task Check(Guid terminalId, string printerName, CancellationToken ct) =>
        Ask(terminalId, CheckMethod, new { printer = printerName }, CheckTimeout, "The terminal did not answer.", ct);

    private async Task Ask(Guid terminalId, string method, object payload, TimeSpan timeout, string noAnswer, CancellationToken ct)
    {
        var connection = presence.ConnectionsOf(terminalId).LastOrDefault()
            ?? throw new PrinterTransportException("The terminal this printer is plugged into is not connected. Is the Tausi Terminal app running?");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        PrintResult result;
        try
        {
            result = await hub.Clients.Client(connection).InvokeAsync<PrintResult>(method, payload, limit.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HubException or IOException)
        {
            throw new PrinterTransportException(noAnswer, ex);
        }
        if (!result.Ok) throw new PrinterTransportException(result.Error ?? "The terminal could not print.");
    }
}

/// <summary>Publishes terminal.status when a terminal comes online or goes offline (30 s after its last connection).</summary>
public sealed class TerminalPresenceMonitor(TerminalPresence presence, IPosEvents events, TimeProvider clock, ILogger<TerminalPresenceMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        while (await Tick(timer, stoppingToken))
        {
            try
            {
                foreach (var (terminalId, online) in presence.TakeChanges())
                    await events.Publish(PosEventNames.TerminalStatus, new { terminalId, online }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not publish terminal status");
            }
        }
    }

    private static async Task<bool> Tick(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
