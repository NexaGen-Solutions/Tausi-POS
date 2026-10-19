using Nexagen.Domain.Devices;
using Nexagen.Domain.Printing;

namespace Nexagen.Application.Printing;

/// <summary>Sends a print job's bytes to a printer. Throws <see cref="PrinterTransportException"/> on failure.</summary>
public interface IPrinterTransport
{
    /// <summary>The connection mode this transport serves; null for a transport that serves every mode (the file transport).</summary>
    PrinterConnection? Mode { get; }

    Task Send(Printer printer, PrintJob job, CancellationToken ct);
}

public class PrinterTransportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reaches a terminal app to print on its local printer ("Via terminal").</summary>
public interface ITerminalPrintChannel
{
    Task Print(Guid terminalId, string printerName, byte[] content, CancellationToken ct);
}
