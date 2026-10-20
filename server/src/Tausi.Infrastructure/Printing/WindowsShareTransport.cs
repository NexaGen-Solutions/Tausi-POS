using Tausi.Application.Printing;
using Tausi.Domain.Devices;
using Tausi.Domain.Printing;

namespace Tausi.Infrastructure.Printing;

/// <summary>
/// A printer shared from a Windows PC (\\PC\Share). Writing raw bytes to the share sends them to
/// the printer unchanged. Only possible when Tausi Server runs on Windows.
/// </summary>
public sealed class WindowsShareTransport : IPrinterTransport
{
    public PrinterConnection? Mode => PrinterConnection.WindowsShare;

    public async Task Send(Printer printer, PrintJob job, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PrinterTransportException(
                "Windows share printers only work when Tausi Server runs on Windows. " +
                "Use a network or via-terminal connection for this printer.");
        if (!printer.Address.StartsWith(@"\\", StringComparison.Ordinal))
            throw new PrinterTransportException(@"The share name must look like \\PC-NAME\PrinterShare.");
        try
        {
            await using var share = new FileStream(printer.Address, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            await share.WriteAsync(job.Content, ct);
            await share.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PrinterTransportException($"Cannot print to {printer.Address}: {ex.Message}", ex);
        }
    }
}
