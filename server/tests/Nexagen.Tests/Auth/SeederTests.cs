using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nexagen.Application.Auth;
using Nexagen.Domain.Devices;
using Nexagen.Infrastructure.Seeding;
using Nexagen.Tests.Support;

namespace Nexagen.Tests.Auth;

public class SeederTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private Seeder Seeder() => new(_db.Context, new FastPinHasher(), NullLogger<Seeder>.Instance);

    [Fact]
    public async Task In_development_demo_printers_print_to_files()
    {
        await Seeder().SeedDemo("pw", CancellationToken.None, filePrinters: true);

        (await _db.NewContext().Printers.ToListAsync()).Should().NotBeEmpty()
            .And.OnlyContain(p => p.ConnectionMode == PrinterConnection.File);
    }

    [Fact]
    public async Task Elsewhere_they_are_windows_share_placeholders()
    {
        await Seeder().SeedDemo("pw", CancellationToken.None);

        (await _db.NewContext().Printers.ToListAsync()).Should()
            .OnlyContain(p => p.ConnectionMode == PrinterConnection.WindowsShare && p.Address.StartsWith(@"\\"));
    }

    [Fact]
    public async Task Seeding_again_in_development_moves_untouched_placeholders_to_files()
    {
        await Seeder().SeedDemo("pw", CancellationToken.None);
        await Seeder().SeedDemo("pw", CancellationToken.None, filePrinters: true);

        (await _db.NewContext().Printers.ToListAsync()).Should().OnlyContain(p => p.ConnectionMode == PrinterConnection.File);
    }

    [Fact]
    public async Task Seeded_users_record_their_pin_length_and_max_length_is_just_a_number()
    {
        var users = await Seeder().SeedDemo("pw", CancellationToken.None);

        users.Should().OnlyContain(u => u.Pin.Length == 4);
        var session = new FakeSession { Terminal = new Application.Security.DeviceTerminal(Guid.NewGuid(), "Counter A") };
        (await new GetMaxPinLength(_db.Context, session).Execute(CancellationToken.None)).Should().Be(4);
    }

    [Fact]
    public async Task Require_pin_change_flags_the_user()
    {
        await Seeder().SeedDemo("pw", CancellationToken.None);

        (await Seeder().RequirePinChange("edna", CancellationToken.None)).Should().BeTrue();
        (await Seeder().RequirePinChange("nobody", CancellationToken.None)).Should().BeFalse();
        (await _db.NewContext().Users.SingleAsync(u => u.Name == "edna")).MustChangePin.Should().BeTrue();
    }

    [Fact]
    public async Task One_printer_per_terminal_with_shared_dispense_templates()
    {
        await Seeder().SeedDemo("pw", CancellationToken.None, filePrinters: true);

        var db = _db.NewContext();
        var printers = await db.Printers.ToDictionaryAsync(p => p.Name);
        printers.Keys.Should().BeEquivalentTo(["Ticket Printer 1", "Pillar 1", "Ticket Printer 2", "Pillar 2"]);
        var receiptsAt = new Dictionary<string, string>
        {
            ["Counter A"] = "Ticket Printer 1", ["Pillar 1"] = "Ticket Printer 1",
            ["Counter B"] = "Ticket Printer 2", ["Pillar 2"] = "Ticket Printer 2",
        };
        foreach (var t in await db.Terminals.ToListAsync())
        {
            var own = printers.Values.Single(p => p.OwningTerminalId == t.Id);
            t.DispensePrinterId.Should().Be(own.Id, $"{t.Name} dispenses on its own printer");
            t.ReceiptPrinterId.Should().Be(printers[receiptsAt[t.Name]].Id, $"{t.Name}'s receipts print at its counter");
        }
        printers["Ticket Printer 1"].DispenseTemplate.Should().Be("Dispense A");
        printers["Pillar 1"].DispenseTemplate.Should().Be("Dispense A");
        printers["Pillar 1"].DispenseHeader.Should().Be("Counter A");
        printers["Ticket Printer 2"].DispenseTemplate.Should().Be("Dispense B");
        printers["Pillar 2"].DispenseHeader.Should().Be("Counter B");
    }

    [Fact]
    public async Task Seeding_again_converts_the_two_printer_setup_without_losing_anything()
    {
        // The old seed: two printers, the pillars sharing their counter's printer.
        await Seeder().SeedDemo("pw", CancellationToken.None);
        var db = _db.Context;
        foreach (var name in new[] { "Pillar 1", "Pillar 2" })
            db.Printers.Remove(await db.Printers.SingleAsync(p => p.Name == name));
        await db.SaveChangesAsync();
        var tp1 = await db.Printers.SingleAsync(p => p.Name == "Ticket Printer 1");
        var tp2 = await db.Printers.SingleAsync(p => p.Name == "Ticket Printer 2");
        foreach (var (terminal, printer) in new[] { ("Pillar 1", tp1), ("Pillar 2", tp2) })
        {
            var t = await db.Terminals.SingleAsync(x => x.Name == terminal);
            t.DispensePrinterId = printer.Id;
            t.ReceiptPrinterId = printer.Id;
        }
        tp1.DispenseTemplate = null;
        db.PrintJobs.Add(new Domain.Printing.PrintJob { PrinterId = tp1.Id, TextPreview = "old slip" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Seeder().SeedDemo("pw", CancellationToken.None);

        var read = _db.NewContext();
        (await read.Printers.CountAsync()).Should().Be(4);
        var pillar1 = await read.Terminals.SingleAsync(t => t.Name == "Pillar 1");
        var pillar1Printer = await read.Printers.SingleAsync(p => p.Name == "Pillar 1");
        pillar1.DispensePrinterId.Should().Be(pillar1Printer.Id);
        pillar1.ReceiptPrinterId.Should().Be(tp1.Id, "receipts stay at the counter");
        (await read.Printers.SingleAsync(p => p.Name == "Ticket Printer 1")).DispenseTemplate.Should().Be("Dispense A");
        (await read.Terminals.SingleAsync(t => t.Name == "Counter A")).DispensePrinterId.Should().Be(tp1.Id);
        (await read.PrintJobs.SingleAsync()).PrinterId.Should().Be(tp1.Id, "old jobs stay where they were");
    }

    [Fact]
    public async Task Routes_set_by_hand_are_left_alone()
    {
        await Seeder().SeedDemo("pw", CancellationToken.None);
        var db = _db.Context;
        var pillar2 = await db.Terminals.SingleAsync(t => t.Name == "Pillar 2");
        var tp1 = await db.Printers.SingleAsync(p => p.Name == "Ticket Printer 1");
        pillar2.ReceiptPrinterId = tp1.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Seeder().SeedDemo("pw", CancellationToken.None);

        (await _db.NewContext().Terminals.SingleAsync(t => t.Name == "Pillar 2")).ReceiptPrinterId.Should().Be(tp1.Id);
    }

    [Fact]
    public async Task Seeding_again_moves_pillar_receipts_from_the_pillar_printer_back_to_the_counter()
    {
        // As the previous seed left it: each pillar printing its own receipts.
        await Seeder().SeedDemo("pw", CancellationToken.None);
        var db = _db.Context;
        foreach (var name in new[] { "Pillar 1", "Pillar 2" })
        {
            var terminal = await db.Terminals.SingleAsync(t => t.Name == name);
            terminal.ReceiptPrinterId = (await db.Printers.SingleAsync(p => p.Name == name)).Id;
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Seeder().SeedDemo("pw", CancellationToken.None);

        var read = _db.NewContext();
        var tp2 = await read.Printers.SingleAsync(p => p.Name == "Ticket Printer 2");
        (await read.Terminals.SingleAsync(t => t.Name == "Pillar 2")).ReceiptPrinterId.Should().Be(tp2.Id);
    }
}
