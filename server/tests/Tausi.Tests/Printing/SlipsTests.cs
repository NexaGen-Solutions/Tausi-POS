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

    // ---- Cash slips (Task 9) ----

    private static Tausi.Application.Cash.ShiftDto CashUpShift(decimal counted, IReadOnlyDictionary<string, int> notes, decimal coins) =>
        new(Guid.Empty, "Closed", Guid.Empty, "CAROL", Guid.Empty, "Counter A", At.AddHours(-6), At,
            2000m, 370m, 200m, 1000m, 1170m, 2, 1300m, 1, 270m, true, true, notes, coins, counted, counted - 1170m,
            null, "MAU", 3, 1, 3, [1000, 500, 200, 100, 50],
            [new(Guid.Empty, "edna", 370m, 0), new(Guid.Empty, "CYNTHIA", 0m, 1)],
            [new(Guid.Empty, "PayOut", 200m, "Transport", "Taxi for ice", "CAROL", At.AddHours(-2)),
             new(Guid.Empty, "Drop", 1000m, null, "To the safe", "CAROL", At.AddHours(-1))],
            []);

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_cash_up_slip_shows_the_drawer_the_count_and_the_sign_off(int columns) =>
        Golden($"cashup-{columns}", Tausi.Application.Cash.CashSlips.CashUp(
            CashUpShift(1170m, new Dictionary<string, int> { ["1000"] = 1, ["100"] = 1, ["50"] = 1 }, 20m),
            "Mau's Village", at => at, new SlipStyle(columns, true, null)));

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_short_cash_up_says_short(int columns) =>
        Golden($"cashup-short-{columns}", Tausi.Application.Cash.CashSlips.CashUp(
            CashUpShift(1070m, new Dictionary<string, int> { ["1000"] = 1, ["50"] = 1 }, 20m),
            "Mau's Village", at => at, new SlipStyle(columns, true, null)));

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void Pay_out_and_drop_slips(int columns)
    {
        var style = new SlipStyle(columns, true, null);
        Golden($"payout-{columns}", Tausi.Application.Cash.CashSlips.Movement(
            new Tausi.Domain.Cash.DrawerMovement { Kind = Tausi.Domain.Cash.DrawerMovementKind.PayOut, Amount = 200m, CategoryName = "Transport", Note = "Taxi for ice", At = At },
            "Mau's Village", "Counter A", "CAROL", "MAU", at => at, style));
        Golden($"drop-{columns}", Tausi.Application.Cash.CashSlips.Movement(
            new Tausi.Domain.Cash.DrawerMovement { Kind = Tausi.Domain.Cash.DrawerMovementKind.Drop, Amount = 1000m, At = At },
            "Mau's Village", "Counter A", "CAROL", null, at => at, style));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_waiter_slip_lists_sales_money_brought_and_open_bills(int columns) =>
        Golden($"waiter-{columns}", Tausi.Application.Cash.CashSlips.Waiter(
            new Tausi.Application.Cash.MySalesDto("edna", At.AddHours(-8), 3400m, 6, 1370m, 1000m, 270m,
                [new(Guid.Empty, 1047, "Table 7", 540m)], []),
            "Mau's Village", at => at, new SlipStyle(columns, true, null)));

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void The_day_report(int columns) =>
        Golden($"day-report-{columns}", Tausi.Application.Cash.CashSlips.DayReport(
            new Tausi.Application.Day.DaySummaryDto(4, 2240m, 0m, 300m, 250m, 1931.04m, 308.96m,
                [new("VAT 16%", 1931.04m, 308.96m)],
                [new("Cash", 1, 370m), new("M-Pesa", 2, 1300m), new("Card", 2, 570m)], 2240m, 0m,
                [new("CAROL", "Counter A", 0m)], 200m),
            "Mau's Village", At.AddHours(-10), At, "MAU", new SlipStyle(columns, true, null)));

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

    /// <summary>Order lines as the server builds them: amounts, discount shares and VAT after the discount.</summary>
    private static OrderLine OL(string name, decimal qty, decimal price, string? options = null, bool exempt = false, bool gift = false)
    {
        var line = new OrderLine { Name = name, ShortName = name, Options = options, UnitPrice = price, VatRate = exempt ? 0 : 16m, VatExempt = exempt, Status = OrderLineStatus.Printed, IsGift = gift };
        line.SetQuantity(qty);
        return line;
    }

    /// <summary>Table 7's ticket #1042 for KSh 1,370 (the wireframe's bill): 2 Tusker, Guiness, water, 2 sodas, punch.</summary>
    private static Ticket Table7(params OrderLine[] extra) => new()
    {
        Number = 1042,
        Lines = [OL("Tusker Lager", 2, 250m, "Cold"), OL("Guiness", 1, 300m), OL("Mau's Water 1L", 1, 100m, exempt: true),
            OL("Sodas 300ml", 2, 100m, "Cold"), OL("Pineapple Punch", 1, 270m), .. extra],
    };

    private static ReceiptSlip Receipt(IReadOnlyList<ReceiptPayment> payments, Ticket? ticket = null, bool copy = false, bool drawer = false)
    {
        var t = ticket ?? Table7();
        return new ReceiptSlip(Mau, 2041, t.Number, copy, "Table 7", "edna", "CAROL", "Counter A", At,
            t.Lines.Where(l => l.Counts).Select(Tausi.Application.Tickets.TicketPrints.ToSlipLine).ToList(),
            Tausi.Application.Tickets.TicketPrints.DiscountLabel(t), t.RoundingAmount, payments, drawer);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_cash_receipt_shows_change_and_opens_the_drawer(int columns) =>
        Golden($"receipt-cash-{columns}", Slips.Receipt(Receipt([new("Cash", 1370m, 1500m, 130m, null, "edna")], drawer: true), new SlipStyle(columns, true, null)));

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_receipt_with_m_pesa_and_cash_lists_both_and_who_brought_them(int columns) =>
        Golden($"receipt-mpesa-cash-{columns}", Slips.Receipt(Receipt(
            [new("M-Pesa", 1000m, null, null, "SJK3ABC123", "edna"), new("Cash", 370m, 500m, 130m, null, "edna")], drawer: true),
            new SlipStyle(columns, true, null)));

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_receipt_with_a_discount_and_a_gift_paid_by_card(int columns)
    {
        var ticket = Table7(OL("Guiness", 1, 300m, gift: true));
        ticket.SetDiscount(DiscountKind.Percent, 10m, "Regular customer");
        ticket.ApplyRounding(Tausi.Domain.Money.RoundingRule.NearestOne);
        Golden($"receipt-discount-gift-{columns}", Slips.Receipt(Receipt([new("Card", ticket.AmountDue, null, null, "1234", "WAMBOO")], ticket),
            new SlipStyle(columns, true, null)));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_receipt_shows_the_rounding_and_the_amount_due(int columns)
    {
        // 7% off 1,370 is 95.90: 1,274.10 is due as 1,274.
        var ticket = Table7();
        ticket.SetDiscount(DiscountKind.Percent, 7m, "Promotion");
        ticket.ApplyRounding(Tausi.Domain.Money.RoundingRule.NearestOne);
        ticket.RoundingAmount.Should().Be(-0.10m);
        Golden($"receipt-rounding-{columns}", Slips.Receipt(Receipt([new("M-Pesa", 1274m, null, null, "SJK3ABC124", "edna")], ticket),
            new SlipStyle(columns, true, null)));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(32)]
    public void A_reprinted_receipt_says_copy_and_never_opens_the_drawer(int columns) =>
        Golden($"receipt-copy-{columns}", Slips.Receipt(Receipt([new("Cash", 1370m, 1370m, 0m, null, "edna")], copy: true),
            new SlipStyle(columns, true, null)));

    [Fact]
    public void Void_and_receipt_slips_render()
    {
        var style = new SlipStyle(42, true, null);
        Slips.Void(new VoidSlip("Counter A", "Table 7", 1042, At, [new(1, "Guiness", null, 300m)], "Wrong item", "edna", "MAU"), style)
            .Text.Should().Contain("VOID").And.Contain("Wrong item").And.Contain("By: edna · Approved: MAU");
        var receipt = Slips.Receipt(Receipt([new("M-Pesa", 1000m, null, null, "SJK3ABC123", "edna"), new("Cash", 370m, 500m, 130m, null, "edna")]), style);
        receipt.Text.Should().Contain("M-Pesa SJK3ABC123").And.Contain("Change 130").And.Contain("Served by edna");
        receipt.Text.Should().NotContain(Slips.NotAReceiptText);
    }

    [Fact]
    public void The_powered_by_line_is_on_bills_and_receipts_only_when_switched_on()
    {
        var off = new SlipStyle(42, true, null);
        var on = off with { PoweredBy = true };

        Slips.Bill(SampleBill(), off).Text.Should().NotContain(Slips.PoweredByText);
        Slips.Bill(SampleBill(), on).Text.Should().Contain(Slips.PoweredByText);
        Slips.Receipt(Receipt([new("Cash", 1370m, 1370m, 0m, null, "edna")]), on).Text.Should().Contain(Slips.PoweredByText);
        Slips.Receipt(Receipt([new("Cash", 1370m, 1370m, 0m, null, "edna")]), off).Text.Should().NotContain(Slips.PoweredByText);
        Slips.Dispense(new DispenseSlip("Counter A", "Table 7", 1, At, [new DispenseGroup("edna", Slips.SampleLines(16m))]), on).Text
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
