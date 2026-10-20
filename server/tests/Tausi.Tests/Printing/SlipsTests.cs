using FluentAssertions;
using Tausi.Application.Printing;
using Tausi.Domain.Devices;

namespace Tausi.Tests.Printing;

public class SlipsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 19, 42, 5, TimeSpan.FromHours(3));

    private static string[] Lines(PrintDocument d) => d.Text.Split(Environment.NewLine);

    private static PrintDocument Test(int paper) => Slips.Test(
        new TestSlip("Mau's Village", "Ticket Printer 1", "Network · 192.168.1.50:9100", paper, 16m, At),
        new SlipStyle(paper == 58 ? 32 : 42, Cut: true, Logo: null));

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
            .And.Contain("2 x Tusker Lager (Cold)").And.Contain("Total includes VAT 16%")
            .And.Contain("Counter A").And.Contain("20 Oct 2026").And.Contain("19:42:05").And.Contain("Test OK");
        Lines(doc).Should().OnlyContain(l => l.Length <= columns);
        Lines(doc).Count(l => l.Trim() == "[cut]").Should().Be(2, "one cut after the sample bill, one at the end");
    }

    [Fact]
    public void The_sample_bill_is_the_real_bill_layout()
    {
        var style = new SlipStyle(42, true, null);
        var bill = Slips.Bill(new BillSlip("Mau's Village", null, "Table 7", "1042", 1, "edna", Slips.SampleLines, 770m, 16m, 106.21m, null, At), style);

        foreach (var line in Lines(bill).Where(l => l.Trim() is not ("" or "[cut]")))
            Test(80).Text.Should().Contain(line);
    }

    [Fact]
    public void A_later_bill_says_it_replaces_earlier_ones()
    {
        var bill = Slips.Bill(new BillSlip("B", null, "Table 7", "1042", 3, "edna", Slips.SampleLines, 770m, 16m, 106.21m, "Karibu", At), new SlipStyle(42, true, null));

        bill.Text.Should().Contain("Bill 3").And.Contain("Replaces earlier bills").And.Contain("Karibu");
    }

    [Fact]
    public void Dispense_slips_carry_the_header_and_only_their_lines()
    {
        var slip = Slips.Dispense(new DispenseSlip("Counter A", "Table 7", "1042", "edna", [new(2, "Sodas 300ml", "Cold", 200m)], At), new SlipStyle(32, true, null));

        Lines(slip)[0].Trim().Should().Be("Counter A");
        slip.Text.Should().Contain("2 x Sodas 300ml").And.Contain("Cold").And.NotContain("200");
    }

    [Fact]
    public void Without_a_cutter_slips_feed_instead_of_cutting()
    {
        var doc = Slips.Dispense(new DispenseSlip("Counter A", "T", "1", "edna", Slips.SampleLines, At), new SlipStyle(42, Cut: false, null));

        doc.EscPos.AsSpan().IndexOf([(byte)0x1D, (byte)0x56]).Should().Be(-1);
        doc.Text.Should().NotContain("[cut]");
    }

    [Fact]
    public void Void_receipt_and_cash_up_slips_render()
    {
        var style = new SlipStyle(42, true, null);
        Slips.Void(new VoidSlip("Counter A", "Table 7", "1042", [new(1, "Guiness", null, 300m)], "Wrong item", "edna", "MAU", At), style)
            .Text.Should().Contain("VOID").And.Contain("Wrong item").And.Contain("Approved by: MAU");
        var bill = new BillSlip("B", null, "Table 7", "1042", 2, "edna", Slips.SampleLines, 770m, 16m, 106.21m, null, At);
        Slips.Receipt(new ReceiptSlip(bill, [("M-Pesa", "SJK3XXXXX", 500m), ("Cash", null, 300m)], 30m, "CAROL"), style)
            .Text.Should().Contain("M-Pesa SJK3XXXXX").And.Contain("Change").And.Contain("Served by CAROL");
        Slips.CashUp(new CashUpSlip("B", "CAROL", At.AddHours(-8), At, [("Cash settled", 12000m)], 12000m, 11950m), style)
            .Text.Should().Contain("Difference").And.Contain("KSh -50");
    }

    [Fact]
    public void Routing_preview_reads_in_print_order()
    {
        PrintRouting.Preview("Pillar 1", "Ticket Printer 1", "Counter A", "Pillar 1")
            .Should().Be("Print on Pillar 1: 1. Bill → Ticket Printer 1 · 2. Dispense slip \"Counter A\" → Pillar 1");
        PrintRouting.Preview("Counter B", null, "Counter B", null)
            .Should().Be("Print on Counter B: 1. Bill → no printer set · 2. Dispense slip \"Counter B\" → no printer set");
    }
}
