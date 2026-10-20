using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Tausi.Application.Printing;
using Tausi.Domain.Devices;
using Tausi.Domain.Printing;
using Tausi.Infrastructure;
using Tausi.Infrastructure.Printing;

namespace Tausi.Tests.Printing;

public class Network9100TransportTests
{
    [Fact]
    public async Task Bytes_arrive_at_a_tcp_listener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var buffer = new MemoryStream();
            await client.GetStream().CopyToAsync(buffer);
            return buffer.ToArray();
        });

        var printer = new Printer { Name = "P", ConnectionMode = PrinterConnection.Network9100, Address = $"127.0.0.1:{port}" };
        var job = new PrintJob { Content = [0x1B, 0x40, 0x48, 0x69, 0x0A, 0x1D, 0x56, 0x42, 0x00] };
        await new Network9100Transport(Options.Create(new TausiOptions())).Send(printer, job, CancellationToken.None);

        (await received.WaitAsync(TimeSpan.FromSeconds(5))).Should().Equal(job.Content);
    }

    [Fact]
    public async Task Unreachable_printer_gives_a_clear_error()
    {
        // Grab a free port, then close it so nothing listens there.
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var printer = new Printer { Address = $"127.0.0.1:{port}" };
        var act = () => new Network9100Transport(Options.Create(new TausiOptions())).Send(printer, new PrintJob(), CancellationToken.None);

        await act.Should().ThrowAsync<PrinterTransportException>().WithMessage($"*127.0.0.1:{port}*");
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50", 9100)]
    [InlineData("192.168.1.50:9101", "192.168.1.50", 9101)]
    [InlineData("printer.local", "printer.local", 9100)]
    public void Parses_addresses(string address, string host, int port)
    {
        Network9100Transport.ParseAddress(address).Should().Be((host, port));
    }
}

public class WindowsShareTransportTests
{
    [Fact]
    public async Task On_linux_it_explains_that_it_needs_windows()
    {
        if (OperatingSystem.IsWindows()) return;

        var act = () => new WindowsShareTransport().Send(new Printer { Address = @"\\PC\Share" }, new PrintJob(), CancellationToken.None);

        await act.Should().ThrowAsync<PrinterTransportException>().WithMessage("*Windows*");
    }
}

public class FilePrinterTransportTests
{
    [Fact]
    public async Task Writes_a_readable_slip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexagen-print-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new TausiOptions { Printing = { OutputDirectory = dir } });
        var transport = new FilePrinterTransport(options, new FakeEnv(), new FakeTimeProvider());
        var job = new PrintJob { Kind = PrintJobKind.Test, TextPreview = "Mau's Village\nTest OK\n" };

        await transport.Send(new Printer { Name = "Ticket Printer 1" }, job, CancellationToken.None);

        var file = Directory.GetFiles(dir).Single();
        Path.GetFileName(file).Should().Contain("Ticket-Printer-1").And.Contain("Test");
        (await File.ReadAllTextAsync(file)).Should().Contain("Test OK");
        Directory.Delete(dir, true);
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
