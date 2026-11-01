using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Domain.Devices;
using Tausi.Domain.Printing;
using Tausi.Infrastructure;
using Tausi.Infrastructure.Printing;
using Tausi.Tests.Support;

namespace Tausi.Tests.Printing;

public class PrintJobProcessorTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.Zero));
    private readonly RecordingEvents _events = new();
    private readonly FakeTransport _transport = new();
    private readonly PrintJobProcessor _processor;
    private readonly PrintQueue _queue;
    private readonly Printer _printer;

    public PrintJobProcessorTests()
    {
        _printer = new Printer { Name = "Ticket Printer 1", ConnectionMode = PrinterConnection.Network9100, Address = "10.0.0.5" };
        _db.Context.Printers.Add(_printer);
        _db.Context.SaveChanges();
        _processor = new PrintJobProcessor(
            _db.Context, [_transport], Options.Create(new TausiOptions()), _events, _clock,
            NullLogger<PrintJobProcessor>.Instance);
        _queue = new PrintQueue(_db.Context, _clock);
    }

    public void Dispose() => _db.Dispose();

    private async Task<PrintJob> Queue(PrintJobKind kind)
    {
        var job = _queue.Enqueue(_printer, kind, new PrintDocument([0x1B, 0x40, (byte)kind], kind.ToString()));
        await _db.Context.SaveChangesAsync();
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        return job;
    }

    [Fact]
    public async Task Sends_a_job_and_marks_it_sent()
    {
        var job = await Queue(PrintJobKind.Test);

        await _processor.ProcessDue(CancellationToken.None);

        (await _db.NewContext().PrintJobs.SingleAsync()).Status.Should().Be(PrintJobStatus.Sent);
        _transport.Sent.Should().Equal(job.Id);
        (await _db.NewContext().Printers.SingleAsync()).Status.Should().Be(PrinterStatus.Online);
    }

    [Fact]
    public async Task Bill_prints_before_dispense_slip_even_after_a_retry()
    {
        var bill = await Queue(PrintJobKind.Bill);
        var dispense = await Queue(PrintJobKind.Dispense);
        _transport.FailNext = 1;

        await _processor.ProcessDue(CancellationToken.None); // bill fails, waits 2 s
        await _processor.ProcessDue(CancellationToken.None); // dispense must wait behind it
        _transport.Sent.Should().BeEmpty();

        _clock.Advance(TimeSpan.FromSeconds(2));
        await _processor.ProcessDue(CancellationToken.None);
        await _processor.ProcessDue(CancellationToken.None);

        _transport.Sent.Should().Equal(bill.Id, dispense.Id);
    }

    [Fact]
    public async Task Jobs_waiting_when_the_server_restarts_print_after_it_in_order()
    {
        // Task 16, Part C.1: the queue is in the database, so a restart loses nothing and keeps the order.
        var bill = await Queue(PrintJobKind.Bill);
        var dispense = await Queue(PrintJobKind.Dispense);
        var receipt = await Queue(PrintJobKind.Receipt);

        // The "restarted" server: a new context and a new processor over the same database.
        var transport = new FakeTransport();
        var restarted = new PrintJobProcessor(
            _db.NewContext(), [transport], Options.Create(new TausiOptions()), _events, _clock, NullLogger<PrintJobProcessor>.Instance);
        while (await restarted.ProcessDue(CancellationToken.None) > 0) { }

        transport.Sent.Should().Equal(bill.Id, dispense.Id, receipt.Id);
        (await _db.NewContext().PrintJobs.ToListAsync()).Should().OnlyContain(j => j.Status == PrintJobStatus.Sent);
    }

    [Fact]
    public async Task Retries_three_times_with_backoff_then_fails_and_publishes()
    {
        var job = await Queue(PrintJobKind.Bill);
        _transport.FailNext = int.MaxValue;

        foreach (var wait in new[] { 0, 2, 4, 8 })
        {
            _clock.Advance(TimeSpan.FromSeconds(wait));
            (await _processor.ProcessDue(CancellationToken.None)).Should().Be(1);
            (await _processor.ProcessDue(CancellationToken.None)).Should().Be(0, "the next try waits for the backoff");
        }

        var stored = await _db.NewContext().PrintJobs.SingleAsync();
        stored.Status.Should().Be(PrintJobStatus.Failed);
        stored.Attempts.Should().Be(4);
        stored.LastError.Should().Be("printer on fire");
        _events.Published.Should().ContainSingle(e => e.Name == PosEventNames.PrintFailed);
        (await _db.NewContext().Printers.SingleAsync()).Status.Should().Be(PrinterStatus.Error);
    }

    [Fact]
    public async Task A_failed_job_does_not_block_the_next_one_for_good()
    {
        await Queue(PrintJobKind.Bill);
        var next = await Queue(PrintJobKind.Dispense);
        _transport.FailNext = 4;

        foreach (var wait in new[] { 0, 2, 4, 8 })
        {
            _clock.Advance(TimeSpan.FromSeconds(wait));
            await _processor.ProcessDue(CancellationToken.None);
        }
        await _processor.ProcessDue(CancellationToken.None);

        _transport.Sent.Should().Equal(next.Id);
    }

    private sealed class FakeTransport : IPrinterTransport
    {
        public int FailNext { get; set; }
        public List<Guid> Sent { get; } = [];
        public PrinterConnection? Mode => PrinterConnection.Network9100;

        public Task Send(Printer printer, PrintJob job, CancellationToken ct)
        {
            if (FailNext > 0)
            {
                FailNext--;
                throw new PrinterTransportException("printer on fire");
            }
            Sent.Add(job.Id);
            return Task.CompletedTask;
        }
    }
}
