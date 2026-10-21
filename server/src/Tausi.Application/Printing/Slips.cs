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

/// <summary>
/// One line on a slip: "2 x Tusker Lager (Cold)   500". <see cref="Vat"/> is the line's own VAT
/// (stored per line); <see cref="ShortName"/> and <see cref="Note"/> are for the dispense slip.
/// </summary>
public sealed record SlipLine(
    decimal Quantity, string Name, string? Options, decimal Amount,
    decimal UnitPrice = 0, decimal VatRate = 0, decimal Vat = 0, string? ShortName = null, string? Note = null);

/// <summary>The business as printed at the top and bottom of customer slips.</summary>
public sealed record BusinessInfo(string Name, string? Address = null, string? Phone = null, string? Footer = null);

/// <param name="Where">"Table 7", "Pool 1" or "Takeaway · Otieno".</param>
/// <param name="Waiter">Who opened the ticket.</param>
/// <param name="Terminal">Where the bill is printed.</param>
public sealed record BillSlip(
    BusinessInfo Business, int BillNumber, long TicketNumber, bool Reprint, string Where, string Waiter, string Terminal,
    DateTimeOffset At, IReadOnlyList<SlipLine> Lines);

/// <summary>The lines one person added in this round.</summary>
public sealed record DispenseGroup(string Waiter, IReadOnlyList<SlipLine> Lines);

public sealed record DispenseSlip(
    string Header, string Where, long TicketNumber, DateTimeOffset At, IReadOnlyList<DispenseGroup> Groups, string? TicketNote = null);

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
    public const string NotAReceiptText = "This is not a receipt";

    /// <summary>The customer's bill: every line on the ticket, identical lines added together.</summary>
    public static PrintDocument Bill(BillSlip d, SlipStyle style)
    {
        var b = WriteBill(new ReceiptBuilder(style.Columns, style.Cut), d, style);
        WriteFooter(b, d.Business, style);
        return b.Text(NotAReceiptText, TextStyle.Center | TextStyle.Small).Cut().Build();
    }

    /// <summary>Lines with the same item, options and price, added together (the bill shows them once).</summary>
    public static IReadOnlyList<SlipLine> AddUp(IEnumerable<SlipLine> lines) =>
        lines.GroupBy(l => (l.Name, l.Options, l.UnitPrice, l.VatRate))
            .Select(g => g.First() with
            {
                Quantity = g.Sum(l => l.Quantity),
                Amount = g.Sum(l => l.Amount),
                Vat = g.Sum(l => l.Vat),
            })
            .ToList();

    private static ReceiptBuilder WriteBill(ReceiptBuilder b, BillSlip d, SlipStyle style)
    {
        b.Logo(style.Logo).Text(d.Business.Name, TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight);
        if (!string.IsNullOrWhiteSpace(d.Business.Address)) b.Text(d.Business.Address, TextStyle.Center);
        if (!string.IsNullOrWhiteSpace(d.Business.Phone)) b.Text(d.Business.Phone, TextStyle.Center);
        b.Separator().Text($"BILL {d.BillNumber} · Ticket #{d.TicketNumber}", TextStyle.Center | TextStyle.Bold);
        if (d.BillNumber > 1) b.Text("Replaces earlier bills", TextStyle.Center);
        if (d.Reprint) b.Text("REPRINT", TextStyle.Center | TextStyle.Bold);
        b.Separator()
            .Columns2(d.Where, d.At.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture))
            .Columns2($"Waiter: {d.Waiter}", d.Terminal)
            .Separator();

        var lines = AddUp(d.Lines);
        foreach (var line in lines) b.Columns2(LineText(line), Amount(line.Amount));
        b.Separator().Columns2("TOTAL", Money(lines.Sum(l => l.Amount), style.Currency), TextStyle.Bold | TextStyle.DoubleHeight);

        // One line per VAT rate, from the VAT stored on each line. The amount is never split over two
        // lines: on narrow paper the label is shortened.
        foreach (var rate in lines.Where(l => l.VatRate > 0).GroupBy(l => l.VatRate).OrderByDescending(g => g.Key))
        {
            var vat = Money(rate.Sum(l => l.Vat), style.Currency);
            var full = $"Total includes VAT {Qty(rate.Key)}%: {vat}";
            if (full.Length <= style.Columns) b.Text(full);
            else b.Columns2($"Includes VAT {Qty(rate.Key)}%", vat);
        }
        var exempt = lines.Where(l => l.VatRate == 0).Sum(l => l.Amount);
        if (lines.Any(l => l.VatRate == 0)) b.Columns2("VAT exempt:", Money(exempt, style.Currency));
        return b;
    }

    /// <summary>The business's footer, then "Powered by Tausi POS" when switched on.</summary>
    private static ReceiptBuilder WriteFooter(ReceiptBuilder b, BusinessInfo business, SlipStyle style)
    {
        if (!string.IsNullOrWhiteSpace(business.Footer)) b.Separator().Text(business.Footer, TextStyle.Center);
        if (style.PoweredBy) b.Feed().Text(PoweredByText, TextStyle.Center);
        return b;
    }

    /// <summary>For the bar: no prices, short names in large text, options and notes underneath.</summary>
    public static PrintDocument Dispense(DispenseSlip d, SlipStyle style) => WriteDispense(new ReceiptBuilder(style.Columns, style.Cut), d).Cut().Build();

    private static ReceiptBuilder WriteDispense(ReceiptBuilder b, DispenseSlip d)
    {
        b.Text(d.Header.ToUpperInvariant(), TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Columns2($"{d.Where} · #{d.TicketNumber}", d.At.ToString("HH:mm", CultureInfo.InvariantCulture));
        foreach (var group in d.Groups)
        {
            b.Separator().Text($"By: {group.Waiter}", TextStyle.Bold);
            foreach (var line in group.Lines)
            {
                b.Text($"{Qty(line.Quantity)} x {line.ShortName ?? line.Name}", TextStyle.Bold | TextStyle.DoubleHeight);
                if (line.Options is { Length: > 0 } options) b.Text("   " + options.ToUpperInvariant());
                if (line.Note is { Length: > 0 } note) b.Text("   Note: " + note);
            }
        }
        b.Separator();
        if (!string.IsNullOrWhiteSpace(d.TicketNote)) b.Text($"Ticket note: {d.TicketNote}", TextStyle.Bold);
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

    /// <summary>The final receipt (Task 8): the bill's lines and totals, then the payments.</summary>
    public static PrintDocument Receipt(ReceiptSlip d, SlipStyle style)
    {
        var b = WriteBill(new ReceiptBuilder(style.Columns, style.Cut), d.Bill, style).Separator();
        foreach (var (method, reference, amount) in d.Payments)
            b.Columns2(reference is null ? method : $"{method} {reference}", Money(amount, style.Currency));
        if (d.Change > 0) b.Columns2("Change", Money(d.Change, style.Currency), TextStyle.Bold);
        b.Text($"Served by {d.Cashier}");
        return WriteFooter(b, d.Bill.Business, style).Cut().Build();
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
    public static IReadOnlyList<SlipLine> SampleLines(decimal vatRate) =>
    [
        Sample(2, "Tusker Lager", "Tusker", "Cold", 250m, vatRate),
        Sample(1, "Pineapple Punch", "Pineapple Punch", null, 270m, vatRate),
    ];

    private static SlipLine Sample(decimal quantity, string name, string shortName, string? options, decimal price, decimal rate)
    {
        var amounts = VatCalculator.ForLine(price, quantity, rate);
        return new SlipLine(quantity, name, options, amounts.Gross, price, rate, amounts.Vat, shortName);
    }

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

        var lines = SampleLines(d.VatRate);
        var business = new BusinessInfo(d.Business);
        WriteBill(b, new BillSlip(business, 1, 1042, false, "Table 7", "edna", "Counter A", d.At, lines), style with { Logo = null });
        WriteFooter(b, business, style).Text(NotAReceiptText, TextStyle.Center | TextStyle.Small).Cut();
        WriteDispense(b, new DispenseSlip("Counter A", "Table 7", 1042, d.At, [new DispenseGroup("edna", lines)]))
            .Columns2("Date", d.At.ToString("dd MMM yyyy", CultureInfo.InvariantCulture))
            .Columns2("Time", d.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            .Text("Test OK", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Feed(2)
            .Cut();
        return b.Build();
    }
}
