using Tausi.Domain.Devices;
using Tausi.Domain.Printing;

namespace Tausi.Application.Printing;

/// <summary>Sends a print job's bytes to a printer. Throws <see cref="PrinterTransportException"/> on failure.</summary>
public interface IPrinterTransport
{
    /// <summary>The connection mode this transport serves.</summary>
    PrinterConnection? Mode { get; }

    Task Send(Printer printer, PrintJob job, CancellationToken ct);
}

public class PrinterTransportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reaches a terminal app to print on its local printer ("Via terminal").</summary>
public interface ITerminalPrintChannel
{
    Task Print(Guid terminalId, string printerName, byte[] content, CancellationToken ct);
}
