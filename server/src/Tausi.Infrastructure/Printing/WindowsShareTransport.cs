using Tausi.Application.Printing;
using Tausi.Domain.Devices;
using Tausi.Domain.Printing;

namespace Tausi.Infrastructure.Printing;

/// <summary>
/// A printer shared from a Windows PC (\\PC\Share), printed through the Windows spooler in RAW mode.
/// Only possible when Tausi Server runs on Windows.
/// </summary>
public sealed class WindowsShareTransport : IPrinterTransport
{
    public const string NeedsWindows = "Windows share printing needs the server to run on Windows.";

    public PrinterConnection? Mode => PrinterConnection.WindowsShare;

    public Task Send(Printer printer, PrintJob job, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PrinterTransportException(NeedsWindows);
            WinSpool.PrintRaw(printer.Address, job.Content, $"Tausi {job.Kind}");
        }, ct);
    }
}
