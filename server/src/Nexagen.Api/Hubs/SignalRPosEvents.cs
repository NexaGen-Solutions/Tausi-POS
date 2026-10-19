using Microsoft.AspNetCore.SignalR;
using Nexagen.Application.Events;
using Nexagen.Application.Printing;

namespace Nexagen.Api.Hubs;

public sealed class SignalRPosEvents(IHubContext<PosHub> hub) : IPosEvents
{
    public Task Publish(string eventName, object payload, CancellationToken ct = default) =>
        hub.Clients.Group(PosHub.BusinessGroup).SendAsync(eventName, payload, ct);
}

/// <summary>
/// Sends a print job to the terminal app the printer is plugged into and waits for its answer
/// (a SignalR client result). The app prints through its local Tauri command.
/// </summary>
public sealed class HubTerminalPrintChannel(IHubContext<PosHub> hub, TerminalConnections terminals) : ITerminalPrintChannel
{
    public const string Method = "print.job";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public sealed record PrintResult(bool Ok, string? Error);

    public async Task Print(Guid terminalId, string printerName, byte[] content, CancellationToken ct)
    {
        var connection = terminals.For(terminalId).LastOrDefault()
            ?? throw new PrinterTransportException("The terminal this printer is plugged into is not connected.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        PrintResult result;
        try
        {
            result = await hub.Clients.Client(connection).InvokeAsync<PrintResult>(
                Method, new { printer = printerName, dataBase64 = Convert.ToBase64String(content) }, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HubException or IOException)
        {
            throw new PrinterTransportException("The terminal did not confirm the print.", ex);
        }
        if (!result.Ok)
            throw new PrinterTransportException(result.Error ?? "The terminal could not print.");
    }
}
