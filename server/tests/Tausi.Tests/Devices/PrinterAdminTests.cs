using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tausi.Application.Printers;
using Tausi.Application.Printing;
using Tausi.Application.Terminals;
using Tausi.Domain.Common;
using Tausi.Domain.Devices;
using Tausi.Domain.Printing;
using Tausi.Infrastructure;
using Tausi.Infrastructure.Printing;

namespace Tausi.Tests.Devices;

public class PrinterAdminTests : IAsyncLifetime
{
    private DevicesFixture _f = null!;

    public async Task InitializeAsync() => _f = await DevicesFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<PrinterRow> Save(Guid? id, PrinterConnection mode, string? address, Guid? owner = null, bool active = true, string name = "Kitchen", long version = 0) =>
        _f.Get<SavePrinter>().Execute(id, new PrinterRequest(name, mode, address, owner, 80, true, false, active, version), CancellationToken.None);

    [Theory]
    [InlineData(PrinterConnection.Network9100, "")]
    [InlineData(PrinterConnection.Network9100, "192.168.1.50:99999")]
    [InlineData(PrinterConnection.WindowsShare, "COUNTER-A\\TP1")]
    [InlineData(PrinterConnection.WindowsShare, "\\\\COUNTER-A")]
    [InlineData(PrinterConnection.ViaTerminal, "POS-80")]
    public async Task The_address_needed_depends_on_the_connection(PrinterConnection mode, string address)
    {
        (await FluentActions.Awaiting(() => Save(null, mode, address)).Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(PrinterCodes.AddressRequired);
    }

    [Fact]
    public async Task Valid_printers_are_saved_tidily()
    {
        (await Save(null, PrinterConnection.Network9100, "192.168.1.50")).Address.Should().Be("192.168.1.50:9100");
        (await Save(null, PrinterConnection.WindowsShare, @"\\COUNTER-A\TP1", name: "Share")).ConnectionText.Should().Be(@"Windows share · \\COUNTER-A\TP1");
        var usb = await Save(null, PrinterConnection.ViaTerminal, "POS-80", _f.Terminal("Pillar 2").Id, name: "USB");
        usb.ConnectionText.Should().Be("Via terminal (USB) · Pillar 2");
        (await Save(null, PrinterConnection.File, null, name: "Dev")).ConnectionText.Should().Be("File (dev)");
    }

    [Fact]
    public async Task A_printer_in_use_cannot_be_deactivated_and_says_where()
    {
        var tp1 = _f.Printer("Ticket Printer 1");
        var ex = await FluentActions.Awaiting(() => Save(tp1.Id, PrinterConnection.File, null, active: false, name: tp1.Name, version: tp1.RowVersion))
            .Should().ThrowAsync<DomainException>();
        ex.Which.Code.Should().Be(PrinterCodes.InUse);
        ex.Which.Message.Should().Contain("Counter A").And.Contain("Pillar 1");
    }

    [Fact]
    public async Task The_list_says_who_uses_each_printer_and_how()
    {
        var rows = await _f.Get<ListPrinters>().Execute(CancellationToken.None);
        var tp1 = rows.Single(r => r.Name == "Ticket Printer 1");
        tp1.UsedBy.Single(u => u.Terminal == "Counter A").Roles.Should().Equal("Dispense \"Counter A\"", "Bills and receipts");
        tp1.UsedBy.Single(u => u.Terminal == "Pillar 1").Roles.Should().Equal("Bills and receipts");
        rows.Single(r => r.Name == "Pillar 1").UsedBy.Single().Roles.Should().Equal("Dispense \"Counter A\"");
    }

    [Fact]
    public async Task Pillar_1_bills_on_ticket_printer_1_and_dispenses_on_its_own_printer()
    {
        var pillar = _f.Terminal("Pillar 1");
        var row = await _f.Get<UpdateTerminal>().Execute(pillar.Id, new TerminalRequest(
            "Pillar 1", null, _f.Printer("Pillar 1").Id, "Counter A", _f.Printer("Ticket Printer 1").Id, true, pillar.RowVersion), CancellationToken.None);

        row.RoutingPreview.Should().Be("Print on Pillar 1: 1. Bill → Ticket Printer 1 · 2. Dispense slip \"Counter A\" → Pillar 1");
    }
}

public class CheckConnectionTests
{
    [Fact]
    public async Task Reachable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await NetworkPrinter.Probe($"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", TimeSpan.FromSeconds(2), CancellationToken.None);
    }

    [Fact]
    public async Task Refused_says_so_in_plain_words()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await FluentActions.Awaiting(() => NetworkPrinter.Probe($"127.0.0.1:{port}", TimeSpan.FromSeconds(2), CancellationToken.None))
            .Should().ThrowAsync<PrinterTransportException>().WithMessage($"127.0.0.1:{port} refused the connection*");
    }

    [Fact]
    public async Task No_answer_says_check_the_printer_is_on()
    {
        // A documentation-only address: nothing answers there, so the connection times out
        // (or, on a network with no route at all, is unreachable).
        var ex = await FluentActions.Awaiting(() => NetworkPrinter.Probe("192.0.2.1:9100", TimeSpan.FromMilliseconds(300), CancellationToken.None))
            .Should().ThrowAsync<PrinterTransportException>();
        ex.Which.Message.Should().Match(m =>
            m == "No answer from 192.0.2.1:9100 — is the printer on and plugged into the network?" || m.StartsWith("Can't reach 192.0.2.1"));
    }
}

public class PrintJobActionsTests : IAsyncLifetime
{
    private DevicesFixture _f = null!;
    private readonly RecordingTransport _transport = new();

    public async Task InitializeAsync() => _f = await DevicesFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private PrintJobProcessor Processor() => new(
        _f.Db, [_transport], Options.Create(new TausiOptions()), _f.Events, _f.Clock, NullLogger<PrintJobProcessor>.Instance);

    private async Task<PrintJob> Queue(string printer, PrintJobKind kind, Guid? ticket = null)
    {
        var p = _f.Db.Printers.Single(x => x.Name == printer);
        var job = new PrintQueue(_f.Db, _f.Clock).Enqueue(p, kind, new PrintDocument([1, 2, 3], kind.ToString()), ticket);
        await _f.Db.SaveChangesAsync();
        _f.Clock.Advance(TimeSpan.FromMilliseconds(5));
        return job;
    }

    private async Task Drain()
    {
        for (var i = 0; i < 6; i++)
        {
            _f.Db.ChangeTracker.Clear();
            await Processor().ProcessDue(CancellationToken.None);
            _f.Clock.Advance(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Retry_keeps_the_bill_before_its_dispense_slip_even_across_printers()
    {
        var ticket = Guid.NewGuid();
        var bill = await Queue("Ticket Printer 1", PrintJobKind.Bill, ticket);
        var dispense = await Queue("Pillar 1", PrintJobKind.Dispense, ticket);
        _transport.FailPrinter = "Ticket Printer 1";

        await Drain();
        (await _f.Read().PrintJobs.SingleAsync(j => j.Id == bill.Id)).Status.Should().Be(PrintJobStatus.Failed);
        _transport.Sent.Should().BeEmpty("the dispense slip waits for its bill");

        _transport.FailPrinter = null;
        await _f.Get<PrintJobActions>().Retry(bill.Id, CancellationToken.None);
        await Drain();

        _transport.Sent.Should().Equal(bill.Id, dispense.Id);
    }

    [Fact]
    public async Task Send_to_another_printer_prints_it_there()
    {
        var job = await Queue("Ticket Printer 2", PrintJobKind.Test);
        _transport.FailPrinter = "Ticket Printer 2";
        await Drain();

        await _f.Get<PrintJobActions>().Redirect(job.Id, new RedirectRequest(_f.Printer("Pillar 2").Id), CancellationToken.None);
        await Drain();

        _transport.SentOn.Should().Equal((job.Id, "Pillar 2"));
        (await _f.Read().AuditEntries.CountAsync(a => a.Action == "print.job_redirected")).Should().Be(1);
    }

    [Fact]
    public async Task Discard_needs_a_reason_and_is_audited()
    {
        var job = await Queue("Ticket Printer 2", PrintJobKind.Test);
        _transport.FailPrinter = "Ticket Printer 2";
        await Drain();
        var actions = _f.Get<PrintJobActions>();

        await FluentActions.Awaiting(() => actions.Discard(job.Id, new DiscardRequest(" "), CancellationToken.None))
            .Should().ThrowAsync<DomainException>();
        await _f.Get<PrintJobActions>().Discard(job.Id, new DiscardRequest("Printed by hand"), CancellationToken.None);

        var stored = await _f.Read().PrintJobs.SingleAsync(j => j.Id == job.Id);
        stored.Status.Should().Be(PrintJobStatus.Discarded);
        stored.DiscardReason.Should().Be("Printed by hand");
        (await _f.Read().AuditEntries.SingleAsync(a => a.Action == "print.job_discarded")).NewValueJson.Should().Contain("Printed by hand");
        (await _f.Get<ListPrinterJobs>().Execute(_f.Printer("Ticket Printer 2").Id, PrintJobStatus.Failed, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_drawer_pulse_goes_only_on_final_receipts_and_only_when_enabled()
    {
        var printer = new Printer { Name = "P", OpenDrawerOnReceipt = true };
        var receipt = new PrintJob { Kind = PrintJobKind.Receipt, Content = [9] };
        var bill = new PrintJob { Kind = PrintJobKind.Bill, Content = [9] };

        PrintJobProcessor.WithDrawerPulse(printer, receipt).Content.Should().Equal([9, .. ReceiptBuilder.DrawerPulse]);
        PrintJobProcessor.WithDrawerPulse(printer, bill).Content.Should().Equal(9);
        printer.OpenDrawerOnReceipt = false;
        PrintJobProcessor.WithDrawerPulse(printer, receipt).Content.Should().Equal(9);
        ReceiptBuilder.DrawerPulse.Should().Equal(0x1B, 0x70, 0x00, 0x19, 0xFA);
        await Task.CompletedTask;
    }

    private sealed class RecordingTransport : IPrinterTransport
    {
        public string? FailPrinter { get; set; }
        public List<Guid> Sent { get; } = [];
        public List<(Guid, string)> SentOn { get; } = [];
        public PrinterConnection? Mode => PrinterConnection.File;

        public Task Send(Printer printer, PrintJob job, CancellationToken ct)
        {
            if (printer.Name == FailPrinter) throw new PrinterTransportException("paper out");
            Sent.Add(job.Id);
            SentOn.Add((job.Id, printer.Name));
            return Task.CompletedTask;
        }
    }
}
