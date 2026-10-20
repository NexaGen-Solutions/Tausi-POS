using System.Globalization;
using Tausi.Domain.Devices;
using Tausi.Domain.Money;

namespace Tausi.Application.Printing;

/// <summary>
/// How a slip is printed on a given printer. <paramref name="PoweredBy"/> adds the "Powered by Tausi POS"
/// line to customer slips (bills and receipts), from the <c>print.poweredByFooter</c> setting.
/// </summary>
public sealed record SlipStyle(int Columns, bool Cut, MonoBitmap? Logo, string Currency = "KES", bool PoweredBy = false)
{
    public static SlipStyle For(Printer printer, MonoBitmap? logo, string currency = "KES", bool poweredBy = false) =>
        new(printer.Columns, printer.CutAfterSlip, logo, currency, poweredBy);
}

/// <summary>One line on a slip: "2 x Tusker Lager (Cold)   500".</summary>
public sealed record SlipLine(decimal Quantity, string Name, string? Options, decimal Amount);

public sealed record BillSlip(
    string Business, string? Address, string Table, string TicketNo, int BillNumber,
    string Waiter, IReadOnlyList<SlipLine> Lines, decimal Total, decimal VatRate, decimal Vat,
    string? Footer, DateTimeOffset At);

public sealed record DispenseSlip(
    string Header, string Table, string TicketNo, string Waiter, IReadOnlyList<SlipLine> Lines, DateTimeOffset At);

public sealed record VoidSlip(
    string Header, string Table, string TicketNo, IReadOnlyList<SlipLine> Lines, string Reason,
    string RequestedBy, string? ApprovedBy, DateTimeOffset At);

public sealed record ReceiptSlip(
    BillSlip Bill, IReadOnlyList<(string Method, string? Reference, decimal Amount)> Payments, decimal Change, string Cashier);

public sealed record CashUpSlip(
    string Business, string Person, DateTimeOffset From, DateTimeOffset To,
    IReadOnlyList<(string Label, decimal Amount)> Rows, decimal Expected, decimal Counted);

public sealed record TestSlip(
    string Business, string PrinterName, string Connection, int PaperWidthMm, decimal VatRate, DateTimeOffset At);

/// <summary>
/// Every slip layout, in one place: bill, dispense, void, receipt, cash-up and test. The test slip
/// prints its sample bill and dispense slip with the same code as real ones, so it is a true preview.
/// Long names wrap instead of being cut off.
/// </summary>
public static class Slips
{
    private static readonly CultureInfo Kenya = CultureInfo.GetCultureInfo("en-KE");

    /// <summary>"1,370", or "1,370.50" when there are cents.</summary>
    public static string Amount(decimal amount) =>
        amount.ToString(decimal.Truncate(amount) == amount ? "#,##0" : "#,##0.00", Kenya);

    public static string Money(decimal amount, string currency) =>
        $"{(currency == "KES" ? "KSh" : currency)} {Amount(amount)}";

    private static string Qty(decimal q) => q.ToString("0.##", CultureInfo.InvariantCulture);

    private static string LineText(SlipLine l) => $"{Qty(l.Quantity)} x {l.Name}" + (l.Options is { Length: > 0 } o ? $" ({o})" : "");

    public const string PoweredByText = "Powered by Tausi POS";

    public static PrintDocument Bill(BillSlip d, SlipStyle style) =>
        PoweredBy(WriteBill(new ReceiptBuilder(style.Columns, style.Cut), d, style), style).Cut().Build();

    /// <summary>The optional last line of a customer slip.</summary>
    private static ReceiptBuilder PoweredBy(ReceiptBuilder b, SlipStyle style) =>
        style.PoweredBy ? b.Feed().Text(PoweredByText, TextStyle.Center) : b;

    private static ReceiptBuilder WriteBill(ReceiptBuilder b, BillSlip d, SlipStyle style)
    {
        b.Logo(style.Logo)
            .Text(d.Business, TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight);
        if (!string.IsNullOrWhiteSpace(d.Address)) b.Text(d.Address, TextStyle.Center);
        b.Separator()
            .Columns2($"{d.Table} · #{d.TicketNo}", $"Bill {d.BillNumber}");
        if (d.BillNumber > 1) b.Text("Replaces earlier bills");
        b.Columns2(d.Waiter, d.At.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture))
            .Separator();
        foreach (var line in d.Lines) b.Columns2(LineText(line), Amount(line.Amount));
        b.Separator()
            .Columns2("Total", Money(d.Total, style.Currency), TextStyle.Bold | TextStyle.DoubleHeight);
        // The VAT amount is never split over two lines: on narrow paper the label is shortened.
        var vatLine = $"Total includes VAT {Qty(d.VatRate)}%: {Money(d.Vat, style.Currency)}";
        if (vatLine.Length <= style.Columns) b.Text(vatLine);
        else b.Columns2($"Includes VAT {Qty(d.VatRate)}%", Money(d.Vat, style.Currency));
        if (!string.IsNullOrWhiteSpace(d.Footer)) b.Separator().Text(d.Footer, TextStyle.Center);
        return b;
    }

    public static PrintDocument Dispense(DispenseSlip d, SlipStyle style) => WriteDispense(new ReceiptBuilder(style.Columns, style.Cut), d).Cut().Build();

    private static ReceiptBuilder WriteDispense(ReceiptBuilder b, DispenseSlip d)
    {
        b.Text(d.Header, TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Columns2($"{d.Table} · #{d.TicketNo}", d.At.ToString("HH:mm", CultureInfo.InvariantCulture))
            .Text(d.Waiter)
            .Separator();
        foreach (var line in d.Lines)
        {
            b.Text($"{Qty(line.Quantity)} x {line.Name}", TextStyle.Bold | TextStyle.DoubleHeight);
            if (line.Options is { Length: > 0 } options) b.Text("   " + options);
        }
        return b;
    }

    public static PrintDocument Void(VoidSlip d, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut)
            .Text("VOID", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Text(d.Header, TextStyle.Center)
            .Columns2($"{d.Table} · #{d.TicketNo}", d.At.ToString("HH:mm", CultureInfo.InvariantCulture))
            .Separator();
        foreach (var line in d.Lines) b.Text($"{Qty(line.Quantity)} x {line.Name}", TextStyle.Bold | TextStyle.DoubleHeight);
        b.Separator()
            .Text($"Reason: {d.Reason}")
            .Text($"Asked by: {d.RequestedBy}");
        if (d.ApprovedBy is not null) b.Text($"Approved by: {d.ApprovedBy}");
        return b.Cut().Build();
    }

    public static PrintDocument Receipt(ReceiptSlip d, SlipStyle style)
    {
        var b = WriteBill(new ReceiptBuilder(style.Columns, style.Cut), d.Bill, style).Separator();
        foreach (var (method, reference, amount) in d.Payments)
            b.Columns2(reference is null ? method : $"{method} {reference}", Money(amount, style.Currency));
        if (d.Change > 0) b.Columns2("Change", Money(d.Change, style.Currency), TextStyle.Bold);
        b.Text($"Served by {d.Cashier}");
        return PoweredBy(b, style).Cut().Build();
    }

    public static PrintDocument CashUp(CashUpSlip d, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut)
            .Text(d.Business, TextStyle.Center | TextStyle.Bold)
            .Text("Cash-up", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Text(d.Person, TextStyle.Center)
            .Text($"{d.From:dd MMM HH:mm} – {d.To:HH:mm}", TextStyle.Center)
            .Separator();
        foreach (var (label, amount) in d.Rows) b.Columns2(label, Money(amount, style.Currency));
        b.Separator()
            .Columns2("Expected", Money(d.Expected, style.Currency), TextStyle.Bold)
            .Columns2("Counted", Money(d.Counted, style.Currency), TextStyle.Bold)
            .Columns2("Difference", Money(d.Counted - d.Expected, style.Currency), TextStyle.Bold);
        return b.Cut().Build();
    }

    /// <summary>Sample lines for the test print (and nothing else).</summary>
    public static IReadOnlyList<SlipLine> SampleLines =>
    [
        new(2, "Tusker Lager", "Cold", 500m),
        new(1, "Pineapple Punch", null, 270m),
    ];

    public static PrintDocument Test(TestSlip d, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut)
            .Logo(style.Logo)
            .Text(d.Business, TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Text("Printer test", TextStyle.Center)
            .Separator()
            .Columns2("Printer", d.PrinterName)
            .Text(d.Connection)
            .Columns2("Paper", $"{d.PaperWidthMm} mm · {style.Columns} columns")
            .Separator()
            .Ruler()
            .Text("Bold line", TextStyle.Bold)
            .Text("Double-height line", TextStyle.DoubleHeight)
            .Text("Centred line", TextStyle.Center)
            .Separator()
            .Text("Bill and dispense slips will look like this:")
            .Feed();

        var lines = SampleLines;
        var total = lines.Sum(l => l.Amount);
        var vat = VatCalculator.Sum(lines.Select(l => VatCalculator.ForLine(l.Amount, 1, d.VatRate))).Vat;
        var sample = WriteBill(b, new BillSlip(d.Business, null, "Table 7", "1042", 1, "edna", lines, total, d.VatRate, vat, null, d.At), style with { Logo = null });
        PoweredBy(sample, style).Cut();
        WriteDispense(b, new DispenseSlip("Counter A", "Table 7", "1042", "edna", lines, d.At))
            .Separator()
            .Columns2("Date", d.At.ToString("dd MMM yyyy", CultureInfo.InvariantCulture))
            .Columns2("Time", d.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            .Text("Test OK", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Feed(2)
            .Cut();
        return b.Build();
    }
}
