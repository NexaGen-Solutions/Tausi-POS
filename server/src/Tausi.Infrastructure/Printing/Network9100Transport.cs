using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Tausi.Application.Printing;
using Tausi.Domain.Devices;
using Tausi.Domain.Printing;

namespace Tausi.Infrastructure.Printing;

/// <summary>Raw ESC/POS over TCP, normally port 9100. Address is "host" or "host:port".</summary>
public sealed class Network9100Transport(IOptions<TausiOptions> options) : IPrinterTransport
{
    public const int DefaultPort = 9100;

    public PrinterConnection? Mode => PrinterConnection.Network9100;

    public async Task Send(Printer printer, PrintJob job, CancellationToken ct)
    {
        var (host, port) = ParseAddress(printer.Address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.Printing.NetworkTimeoutSeconds));
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(job.Content, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PrinterTransportException($"The printer at {host}:{port} did not answer in time.");
        }
        catch (SocketException ex)
        {
            throw new PrinterTransportException($"Cannot reach the printer at {host}:{port} ({ex.SocketErrorCode}).", ex);
        }
        catch (IOException ex)
        {
            throw new PrinterTransportException($"The connection to {host}:{port} broke while printing.", ex);
        }
    }

    public static (string Host, int Port) ParseAddress(string address)
    {
        var a = address.Trim();
        var colon = a.LastIndexOf(':');
        if (colon > 0 && int.TryParse(a[(colon + 1)..], out var port))
            return (a[..colon], port);
        if (a.Length == 0) throw new PrinterTransportException("The printer has no network address.");
        return (a, DefaultPort);
    }
}
