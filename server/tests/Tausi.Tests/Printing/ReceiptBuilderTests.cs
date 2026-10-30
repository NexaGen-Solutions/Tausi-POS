using FluentAssertions;
using Tausi.Application.Printing;

namespace Tausi.Tests.Printing;

public class ReceiptBuilderTests
{
    private static bool Contains(byte[] haystack, params byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    private static string[] Lines(PrintDocument doc) =>
        doc.Text.Split(Environment.NewLine, StringSplitOptions.None)[..^1];

    [Theory]
    [InlineData(80, 42)]
    [InlineData(58, 32)]
    public void Columns_follow_the_paper_width(int paper, int columns)
    {
        ReceiptBuilder.ColumnsFor(paper).Should().Be(columns);
        var doc = new ReceiptBuilder(columns).Separator().Build();
        Lines(doc).Single().Should().HaveLength(columns);
    }

    [Fact]
    public void Starts_by_resetting_the_printer()
    {
        new ReceiptBuilder(42).Build().EscPos[..2].Should().Equal(0x1B, 0x40);
    }

    [Fact]
    public void Bold_double_height_and_centre_use_the_escpos_commands()
    {
        var doc = new ReceiptBuilder(42).Text("Total", TextStyle.Bold | TextStyle.DoubleHeight | TextStyle.Center).Build();

        Contains(doc.EscPos, 0x1B, 0x45, 0x01).Should().BeTrue("bold on");
        Contains(doc.EscPos, 0x1D, 0x21, 0x01).Should().BeTrue("double height on");
        Contains(doc.EscPos, 0x1B, 0x61, 0x01).Should().BeTrue("centre");
        Lines(doc).Single().Should().Be(new string(' ', 18) + "Total");
    }

    [Fact]
    public void Cut_feeds_and_cuts()
    {
        var doc = new ReceiptBuilder(42).Cut().Build();
        Contains(doc.EscPos, 0x1D, 0x56, 0x42, 0x00).Should().BeTrue();
    }

    [Fact]
    public void Long_text_wraps_instead_of_being_cut_off()
    {
        var doc = new ReceiptBuilder(32).Text("Pineapple Punch with extra ice and a slice of lemon").Build();

        Lines(doc).Should().HaveCountGreaterThan(1).And.OnlyContain(l => l.Length <= 32);
        string.Join(" ", Lines(doc)).Should().Be("Pineapple Punch with extra ice and a slice of lemon");
    }

    [Fact]
    public void A_line_break_in_the_text_starts_a_new_line()
    {
        var doc = new ReceiptBuilder(42).Text("Buy Goods\r\n4235672", TextStyle.Center).Build();

        Lines(doc).Should().Equal(new string(' ', 16) + "Buy Goods", new string(' ', 17) + "4235672");
        Contains(doc.EscPos, (byte)'?').Should().BeFalse("the break is not sent to the printer as a character");
    }

    [Fact]
    public void Double_width_text_wraps_at_half_the_columns_and_uses_the_size_command()
    {
        var doc = new ReceiptBuilder(42).Text("Paybill 4235672 Account 77", TextStyle.DoubleWidth | TextStyle.DoubleHeight | TextStyle.Center).Build();

        Contains(doc.EscPos, 0x1D, 0x21, 0x11).Should().BeTrue("double width and height");
        Lines(doc).Should().Equal(new string(' ', 6) + "Paybill 4235672", new string(' ', 11) + "Account 77");
    }

    [Fact]
    public void Two_columns_align_the_amount_to_the_right_edge()
    {
        var doc = new ReceiptBuilder(42).Columns2("2 x Tusker Lager (Cold)", "500").Build();

        var line = Lines(doc).Single();
        line.Should().HaveLength(42).And.StartWith("2 x Tusker Lager (Cold)").And.EndWith(" 500");
    }

    [Fact]
    public void Two_columns_wrap_a_long_name_and_keep_the_amount_on_the_last_line()
    {
        var doc = new ReceiptBuilder(32).Columns2("1 x Gilbeys London Dry Gin 750ml bottle", "2,400").Build();

        var lines = Lines(doc);
        lines.Should().HaveCountGreaterThan(1);
        lines[^1].Should().EndWith(" 2,400").And.HaveLength(32);
    }

    [Fact]
    public void The_punctuation_on_slips_prints_as_plain_characters_not_question_marks()
    {
        ReceiptBuilder.ToPrinterBytes("BILL 12 \u00B7 Ticket #1042").Should().Equal("BILL 12 - Ticket #1042"u8.ToArray());
        ReceiptBuilder.ToPrinterBytes("\u201CDad\u2019s\u201D \u2013 2\u00D73").Should().Equal("\"Dad's\" - 2x3"u8.ToArray());
    }

    [Fact]
    public void Accents_are_dropped_for_the_printer_code_page()
    {
        ReceiptBuilder.ToPrinterBytes("Café").Should().Equal("Cafe"u8.ToArray());
        ReceiptBuilder.ToPrinterBytes("KSh 1,370 €").Should().Equal("KSh 1,370 ?"u8.ToArray());
    }

    [Fact]
    public void Logo_prints_as_a_raster_image()
    {
        var logo = new MonoBitmap(16, 2, [0xFF, 0x00, 0x00, 0xFF]);
        var doc = new ReceiptBuilder(42).Logo(logo).Build();

        // GS v 0, normal mode, 2 bytes per row, 2 rows, then the pixel data.
        Contains(doc.EscPos, 0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x02, 0x00, 0xFF, 0x00, 0x00, 0xFF).Should().BeTrue();
        doc.Text.Should().Contain("[logo]");
    }

    [Fact]
    public void No_logo_prints_nothing()
    {
        new ReceiptBuilder(42).Logo(null).Build().Text.Should().BeEmpty();
    }
}

public class ReceiptTemplateTests
{
    [Fact]
    public void Renders_tags_columns_and_placeholders()
    {
        var doc = ReceiptTemplate.Render(
            "[center][bold]{{business}}\n---\nItem || {{amount}}\n@cut",
            new Dictionary<string, string> { ["business"] = "Demo Bar", ["amount"] = "250" },
            42);

        var lines = doc.Text.Split(Environment.NewLine);
        lines[0].Trim().Should().Be("Demo Bar");
        lines[1].Should().Be(new string('-', 42));
        lines[2].Should().StartWith("Item").And.EndWith("250").And.HaveLength(42);
        lines[3].Trim().Should().Be("[cut]");
    }

    [Fact]
    public void Data_cannot_inject_instructions()
    {
        var doc = ReceiptTemplate.Render("{{name}}", new Dictionary<string, string> { ["name"] = "@cut" }, 42);

        doc.Text.Trim().Should().Be("@cut");
        doc.EscPos.AsSpan().IndexOf([(byte)0x1D, (byte)0x56]).Should().Be(-1);
    }
}
