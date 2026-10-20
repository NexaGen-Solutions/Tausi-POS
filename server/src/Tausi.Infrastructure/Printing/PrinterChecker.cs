using Tausi.Application.Printing;
using Tausi.Domain.Devices;

namespace Tausi.Infrastructure.Printing;

/// <summary>"Check connection" for each connection mode.</summary>
public sealed class PrinterChecker(ITerminalPrintChannel terminals) : IPrinterChecker
{
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(2);

    public Task Check(PrinterConnection mode, string address, Guid? owningTerminalId, CancellationToken ct) => mode switch
    {
        PrinterConnection.Network9100 => NetworkPrinter.Probe(address, NetworkTimeout, ct),
        PrinterConnection.WindowsShare => Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PrinterTransportException(WindowsShareTransport.NeedsWindows);
            WinSpool.CheckExists(address);
        }, ct),
        PrinterConnection.ViaTerminal => terminals.Check(owningTerminalId!.Value, address, ct),
        _ => Task.CompletedTask,
    };
}
