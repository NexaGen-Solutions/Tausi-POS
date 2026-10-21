using System.Runtime.CompilerServices;
using FluentAssertions;
using Tausi.Domain.Service;
using Tausi.Application.Printing;
using Tausi.Domain.Devices;
using Tausi.Domain.Money;

namespace Tausi.Tests.Printing;

public class SlipsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 19, 42, 5, TimeSpan.FromHours(3));

    private static string[] Lines(PrintDocument d) => d.Text.Split(Environment.NewLine);

    private static PrintDocument Test(int paper, bool poweredBy = false) => Slips.Test(
        new TestSlip("Mau's Village", "Ticket Printer 1", "Network · 192.168.1.50:9100", paper, 16m, At),
        new SlipStyle(paper == 58 ? 32 : 42, Cut: true, Logo: null, PoweredBy: poweredBy));

    private static SlipLine Line(decimal qty, string name, decimal price, string? options = null, decimal rate = 16m,
        string? shortName = null, string? note = null, bool exempt = false)
    {
        var amounts = VatCalculator.ForLine(price, qty, rate, exempt);
        return new SlipLine(qty, name, options, amounts.Gross, price, rate, amounts.Vat, shortName ?? name, note, exempt);
    }

    private static readonly BusinessInfo Mau = new("Mau's Village", "Ruaka Road, Nairobi", "Tel 0712 345 678", "Thank you. Karibu tena!");

    /// <summary>
    /// A bill with lines added at different times: two separate Tusker (Cold) lines, a long name, a
    /// zero-rated item and a VAT-exempt one.
    /// </summary>
    private static BillSlip SampleBill(int number = 1, bool reprint = false, BusinessInfo? business = null) => new(
        business ?? Mau, number, 1042, reprint, "Table 7", "edna", "Counter A", At,
        [
            Line(2, "Tusker Lager", 250m, "Cold", shortName: "Tusker"),
            Line(1, "Pineapple Punch", 270m),
            Line(1, "Tusker Lager", 250m, "Cold", shortName: "Tusker"),
            Line(1, "Johnnie Walker Black Label Blended Scotch Whisky 750ml", 4500m, shortName: "JW Black"),
            Line(1, "Fresh Milk 500ml", 70m, rate: 0m, shortName: "Milk"),
            Line(2, "Mau's Water 1L", 50m, rate: 0m, shortName: "Water", exempt: true),
        ]);

    [Theory]
    [InlineData(80, 42)]
    [InlineData(58, 32)]
    public void The_ruler_is_exactly_the_paper_width(int paper, int columns)
    {
        var ruler = Lines(Test(paper)).Single(l => l.StartsWith("1234567890"));

        ruler.Should().HaveLength(columns);
        ruler.Should().Be(string.Concat(Enumerable.Range(1, columns).Select(i => (char)('0' + i % 10))));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(58)]
    public void The_test_print_has_everything_and_fits(int paper)
    {
        var doc = Test(paper);
        var columns = paper == 58 ? 32 : 42;

        doc.Text.Should().Contain("Mau's Village").And.Contain("Ticket Printer 1").And.Contain("192.168.1.50:9100")
            .And.Contain($"{paper} mm · {columns} columns").And.Contain("Bold line").And.Contain("Double-height line")
            .And.Contain("Centred line").And.Contain("Bill and dispense slips")
            .And.Contain("2 x Tusker Lager (Cold)").And.Contain("VAT 16%")
            .And.Contain("COUNTER A").And.Contain("20 Oct 2026").And.Contain("19:42:05").And.Contain("Test OK");
        Lines(doc).Should().OnlyContain(l => l.Length <= columns);
        Lines(doc).Count(l => l.Trim() == "[cut]").Should().Be(2, "one cut after the sample bill, one at the end");
    }

    [Fact]
    public void The_test_prints_sample_bill_and_slip_are_the_real_layouts()
    {
        var lines = Slips.SampleLines(16m);
        var bill = Slips.Bill(new BillSlip(new BusinessInfo("Mau's Village"), 1, 1042, false, "Table 7", "edna", "Counter A", At, lines), new SlipStyle(42, true, null));
        var slip = Slips.Dispense(new DispenseSlip("Counter A", "Table 7", 1042, At, [new DispenseGroup("edna", lines)]), new SlipStyle(42, true, null));

        foreach (var line in Lines(bill).Concat(Lines(slip)).Where(l => l.Trim() is not ("" or "[cut]")))
            Test(80).Text.Should().Contain(line);
    }

    // Golden files: the exact text of each slip at 42 and 32 columns (Golden/*.txt). After a deliberate
    // layout change, run the tests with TAUSI_UPDATE_GOLDEN=1 and review the diff.

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void Bill_1_adds_up_identical_lines_wraps_long_names_and_shows_vat_per_rate(int columns) =>
        Golden($"bill-1-{columns}", Slips.Bill(SampleBill(), new SlipStyle(columns, true, null)));

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_void_slip_has_the_header_item_options_reason_and_who(int columns) =>
        Golden($"void-{columns}", Slips.Void(new VoidSlip("Counter A", "Table 7", 1042, At,
            [Line(1, "Guiness", 300m, "Cold", shortName: "Guiness")], "Wrong item", "edna", "MAU"), new SlipStyle(columns, true, null)));

    /// <summary>
    /// Bill 3 after a void (1 of 2 Tusker), a gift (1 Guiness) and a 10% discount, built the way the
    /// server builds it: from order lines with their discount shares and VAT after the discount.
    /// </summary>
    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_corrected_bill_leaves_voids_off_shows_gifts_and_the_discount(int columns)
    {
        OrderLine L(string name, decimal qty, decimal price, string? options = null, bool exempt = false)
        {
            var line = new OrderLine { Name = name, ShortName = name, Options = options, UnitPrice = price, VatRate = exempt ? 0 : 16m, VatExempt = exempt, Status = OrderLineStatus.Printed };
            line.SetQuantity(qty);
            return line;
        }
        var tusker = L("Tusker Lager", 1, 250m, "Cold");
        var voided = L("Tusker Lager", 1, 250m, "Cold");
        voided.Status = OrderLineStatus.Voided;
        var gift = L("Guiness", 1, 300m, "Cold");
        gift.IsGift = true;
        var ticket = new Ticket { Number = 1042, Lines = [tusker, voided, L("Guiness", 1, 300m, "Cold"), gift, L("Pineapple Punch", 1, 270m), L("Mau's Water 1L", 1, 100m, exempt: true)] };
        ticket.SetDiscount(DiscountKind.Percent, 10m, "Regular customer");

        var slip = new BillSlip(Mau, 3, 1042, false, "Table 7", "edna", "Pillar 1", At,
            ticket.Lines.Where(l => l.Counts).Select(Tausi.Application.Tickets.TicketPrints.ToSlipLine).ToList(),
            Corrected: true, DiscountLabel: Tausi.Application.Tickets.TicketPrints.DiscountLabel(ticket));
        Golden($"bill-corrected-{columns}", Slips.Bill(slip, new SlipStyle(columns, true, null)));
    }

    [Fact]
    public void A_zero_rated_item_is_in_the_vat_lines_at_0_percent_and_only_exempt_items_say_vat_exempt()
    {
        BillSlip Bill(params SlipLine[] lines) => new(Mau, 1, 1042, false, "Table 7", "edna", "Counter A", At, lines);
        var style = new SlipStyle(42, true, null);

        var zeroRated = Slips.Bill(Bill(Line(1, "Fresh Milk 500ml", 70m, rate: 0m)), style).Text;
        zeroRated.Should().Contain("Total includes VAT 0%: KSh 0.00").And.NotContain("exempt");

        var exempt = Slips.Bill(Bill(Line(2, "Mau's Water 1L", 50m, rate: 0m, exempt: true)), style).Text;
        exempt.Should().MatchRegex(@"VAT exempt: +KSh 100\.00").And.NotContain("Total includes VAT");

        // An exempt item with a rate still carries no VAT, and is not in the 16% line.
        var both = Slips.Bill(Bill(Line(1, "Pineapple Punch", 270m), Line(1, "Exempt juice", 116m, rate: 16m, exempt: true)), style).Text;
        both.Should().Contain("Total includes VAT 16%: KSh 37.24").And.MatchRegex(@"VAT exempt: +KSh 116\.00");
    }

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void Bill_2_says_it_replaces_earlier_bills(int columns) =>
        Golden($"bill-2-{columns}", Slips.Bill(SampleBill(number: 2), new SlipStyle(columns, true, null)));

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_reprint_is_marked_under_the_bill_number(int columns) =>
        Golden($"bill-reprint-{columns}", Slips.Bill(SampleBill(number: 3, reprint: true), new SlipStyle(columns, true, null)));

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void Without_a_footer_and_with_powered_by(int columns) =>
        Golden($"bill-no-footer-powered-by-{columns}",
            Slips.Bill(SampleBill(business: Mau with { Footer = null }), new SlipStyle(columns, true, null, PoweredBy: true)));

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_dispense_slip_has_no_prices_short_names_options_and_notes(int columns) =>
        Golden($"dispense-{columns}", Slips.Dispense(new DispenseSlip("Counter A", "Table 7", 1042, At,
            [new DispenseGroup("edna", [
                Line(2, "Tusker Lager", 250m, "Cold", shortName: "Tusker"),
                Line(1, "Pineapple Punch", 270m, note: "No ice, extra straw"),
            ])], TicketNote: "Birthday table"), new SlipStyle(columns, true, null)));

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_dispense_slip_lists_each_waiters_lines(int columns) =>
        Golden($"dispense-two-waiters-{columns}", Slips.Dispense(new DispenseSlip("Counter B", "Pool 1", 1050, At,
            [
                new DispenseGroup("edna", [Line(2, "Tusker Lager", 250m, "Cold", shortName: "Tusker")]),
                new DispenseGroup("Ruth", [Line(1, "Guiness", 300m, "Warm"), Line(3, "Sodas 300ml", 100m, shortName: "Soda")]),
            ]), new SlipStyle(columns, true, null)));

    [Fact]
    public void Identical_lines_are_added_together_but_different_prices_or_options_are_not()
    {
        var added = Slips.AddUp([
            Line(1, "Tusker Lager", 250m, "Cold"), Line(2, "Tusker Lager", 250m, "Cold"),
            Line(1, "Tusker Lager", 250m, "Warm"), Line(1, "Tusker Lager", 230m, "Cold"),
        ]);

        added.Select(l => (l.Quantity, l.Options, l.UnitPrice, l.Amount)).Should().Equal(
            (3m, "Cold", 250m, 750m), (1m, "Warm", 250m, 250m), (1m, "Cold", 230m, 230m));
        added[0].Vat.Should().Be(Line(1, "T", 250m).Vat + Line(2, "T", 250m).Vat, "the stored VAT of each line, added up");
    }

    [Fact]
    public void Bills_print_this_is_not_a_receipt_in_small_text()
    {
        var doc = Slips.Bill(SampleBill(), new SlipStyle(42, true, null));

        Lines(doc).Should().Contain(l => l.Trim() == Slips.NotAReceiptText);
        doc.EscPos.AsSpan().IndexOf([(byte)0x1B, (byte)0x4D, (byte)0x01]).Should().BeGreaterThan(0, "the small font is switched on");
    }

    [Fact]
    public void Without_a_cutter_slips_feed_instead_of_cutting()
    {
        var doc = Slips.Dispense(new DispenseSlip("Counter A", "Table 7", 1, At, [new DispenseGroup("edna", Slips.SampleLines(16m))]),
            new SlipStyle(42, Cut: false, null));

        doc.EscPos.AsSpan().IndexOf([(byte)0x1D, (byte)0x56]).Should().Be(-1);
        doc.Text.Should().NotContain("[cut]");
    }

    [Fact]
    public void Void_receipt_and_cash_up_slips_render()
    {
        var style = new SlipStyle(42, true, null);
        Slips.Void(new VoidSlip("Counter A", "Table 7", 1042, At, [new(1, "Guiness", null, 300m)], "Wrong item", "edna", "MAU"), style)
            .Text.Should().Contain("VOID").And.Contain("Wrong item").And.Contain("By: edna · Approved: MAU");
        var receipt = Slips.Receipt(new ReceiptSlip(SampleBill(), [("M-Pesa", "SJK3XXXXX", 500m), ("Cash", null, 300m)], 30m, "CAROL"), style);
        receipt.Text.Should().Contain("M-Pesa SJK3XXXXX").And.Contain("Change").And.Contain("Served by CAROL");
        receipt.Text.Should().NotContain(Slips.NotAReceiptText);
        Slips.CashUp(new CashUpSlip("B", "CAROL", At.AddHours(-8), At, [("Cash settled", 12000m)], 12000m, 11950m), style)
            .Text.Should().Contain("Difference").And.Contain("KSh -50");
    }

    [Fact]
    public void The_powered_by_line_is_on_bills_and_receipts_only_when_switched_on()
    {
        var off = new SlipStyle(42, true, null);
        var on = off with { PoweredBy = true };

        Slips.Bill(SampleBill(), off).Text.Should().NotContain(Slips.PoweredByText);
        Slips.Bill(SampleBill(), on).Text.Should().Contain(Slips.PoweredByText);
        Slips.Receipt(new ReceiptSlip(SampleBill(), [("Cash", null, 5000m)], 0m, "CAROL"), on).Text.Should().Contain(Slips.PoweredByText);
        Slips.Dispense(new DispenseSlip("Counter A", "Table 7", 1, At, [new DispenseGroup("edna", Slips.SampleLines(16m))]), on).Text
            .Should().NotContain(Slips.PoweredByText);
        Slips.CashUp(new CashUpSlip("B", "CAROL", At.AddHours(-8), At, [("Cash settled", 100m)], 100m, 100m), on).Text
            .Should().NotContain(Slips.PoweredByText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_test_print_shows_the_powered_by_line_on_its_sample_bill_as_set(bool poweredBy)
    {
        Lines(Test(80, poweredBy)).Count(l => l.Trim() == Slips.PoweredByText).Should().Be(poweredBy ? 1 : 0);
    }

    [Fact]
    public void Routing_preview_reads_in_print_order()
    {
        PrintRouting.Preview("Pillar 1", "Ticket Printer 1", "Counter A", "Pillar 1")
            .Should().Be("Print on Pillar 1: 1. Bill → Ticket Printer 1 · 2. Dispense slip \"Counter A\" → Pillar 1");
        PrintRouting.Preview("Counter B", null, "Counter B", null)
            .Should().Be("Print on Counter B: 1. Bill → no printer set · 2. Dispense slip \"Counter B\" → no printer set");
    }

    private static void Golden(string name, PrintDocument doc, [CallerFilePath] string source = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(source)!, "Golden", name + ".txt");
        var text = doc.Text.Replace("\r\n", "\n");
        if (Environment.GetEnvironmentVariable("TAUSI_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        File.Exists(path).Should().BeTrue($"the golden file {name}.txt exists (run with TAUSI_UPDATE_GOLDEN=1 to create it)");
        text.Should().Be(File.ReadAllText(path).Replace("\r\n", "\n"), $"the slip matches Golden/{name}.txt");
        text.Split('\n').Should().OnlyContain(l => l.Length <= (name.EndsWith("-32") ? 32 : 42), "nothing runs past the paper width");
    }
}
