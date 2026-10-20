using Tausi.Application.Printing;
using Tausi.Domain.Devices;
using Tausi.Domain.Printing;

namespace Tausi.Infrastructure.Printing;

/// <summary>Hands the job to the terminal app the printer is plugged into, which prints it locally.</summary>
public sealed class ViaTerminalTransport(ITerminalPrintChannel channel) : IPrinterTransport
{
    public PrinterConnection? Mode => PrinterConnection.ViaTerminal;

    public Task Send(Printer printer, PrintJob job, CancellationToken ct)
    {
        if (printer.OwningTerminalId is not { } terminalId)
            throw new PrinterTransportException("Pick the terminal this printer is plugged into.");
        return channel.Print(terminalId, printer.Address, job.Content, ct);
    }
}
