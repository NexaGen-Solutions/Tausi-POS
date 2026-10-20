using System.Net.Sockets;
using Tausi.Application.Printing;

namespace Tausi.Infrastructure.Printing;

/// <summary>Opens a TCP connection to a network printer, with errors in plain words.</summary>
public static class NetworkPrinter
{
    public static async Task<TcpClient> Connect(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, limit.Token);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new PrinterTransportException($"No answer from {host}:{port} — is the printer on and plugged into the network?");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new PrinterTransportException(ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused =>
                    $"{host}:{port} refused the connection — is that the printer's address, and is its port {port}?",
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                    $"Can't find \"{host}\" on the network — check the address.",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                    $"Can't reach {host} from the server — is it on the same network?",
                SocketError.TimedOut =>
                    $"No answer from {host}:{port} — is the printer on and plugged into the network?",
                _ => $"Can't connect to {host}:{port} ({ex.SocketErrorCode}).",
            }, ex);
        }
    }

    /// <summary>Connects and disconnects, to see whether the printer answers.</summary>
    public static async Task Probe(string address, TimeSpan timeout, CancellationToken ct)
    {
        var (host, port) = Network9100Transport.ParseAddress(address);
        using var client = await Connect(host, port, timeout, ct);
    }
}
