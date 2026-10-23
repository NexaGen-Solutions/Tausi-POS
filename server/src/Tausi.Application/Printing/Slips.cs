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
/// <see cref="VatExempt"/> lines are listed as "VAT exempt"; a zero-rated line (rate 0, not exempt)
/// is part of "Total includes VAT 0%".
/// </summary>
public sealed record SlipLine(
    decimal Quantity, string Name, string? Options, decimal Amount,
    decimal UnitPrice = 0, decimal VatRate = 0, decimal Vat = 0, string? ShortName = null, string? Note = null,
    bool VatExempt = false, bool IsGift = false, decimal Discount = 0);

/// <summary>The business as printed at the top and bottom of customer slips.</summary>
public sealed record BusinessInfo(string Name, string? Address = null, string? Phone = null, string? Footer = null);

/// <param name="Where">"Table 7", "Pool 1" or "Takeaway · Otieno".</param>
/// <param name="Waiter">Who opened the ticket.</param>
/// <param name="Terminal">Where the bill is printed.</param>
/// <param name="Corrected">Printed after a void, gift, discount or price change: "Corrected bill · replaces earlier bills".</param>
/// <param name="DiscountLabel">"Discount 10% (Regular customer)"; the amount is the lines' discount shares.</param>
public sealed record BillSlip(
    BusinessInfo Business, int BillNumber, long TicketNumber, bool Reprint, string Where, string Waiter, string Terminal,
    DateTimeOffset At, IReadOnlyList<SlipLine> Lines, bool Corrected = false, string? DiscountLabel = null);

/// <summary>The lines one person added in this round.</summary>
public sealed record DispenseGroup(string Waiter, IReadOnlyList<SlipLine> Lines);

public sealed record DispenseSlip(
    string Header, string Where, long TicketNumber, DateTimeOffset At, IReadOnlyList<DispenseGroup> Groups, string? TicketNote = null);

/// <summary>For the bar that dispensed the item: stop it, or take it back.</summary>
public sealed record VoidSlip(
    string Header, string Where, long TicketNumber, DateTimeOffset At, IReadOnlyList<SlipLine> Lines, string Reason,
    string RequestedBy, string ApprovedBy);

/// <summary>One payment on the receipt. <see cref="Type"/> is "Cash", "M-Pesa" or "Card".</summary>
public sealed record ReceiptPayment(string Type, decimal Amount, decimal? Tendered, decimal? Change, string? Reference, string BroughtBy);

/// <summary>
/// The final receipt, printed when the ticket is settled. <see cref="Copy"/> for a reprint.
/// <see cref="Rounding"/> is due minus total (e.g. -0.40).
/// </summary>
public sealed record ReceiptSlip(
    BusinessInfo Business, long ReceiptNumber, long TicketNumber, bool Copy, string Where, string Waiter, string Cashier,
    string Terminal, DateTimeOffset At, IReadOnlyList<SlipLine> Lines, string? DiscountLabel, decimal Rounding,
    IReadOnlyList<ReceiptPayment> Payments, bool OpenDrawer = false);

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

    /// <summary>"KSh 0.00", "KSh 206.90": VAT figures always show cents.</summary>
    public static string MoneyCents(decimal amount, string currency) =>
        $"{(currency == "KES" ? "KSh" : currency)} {amount.ToString("#,##0.00", Kenya)}";

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
        lines.GroupBy(l => (l.Name, l.Options, l.UnitPrice, l.VatRate, l.VatExempt, l.IsGift))
            .Select(g => g.First() with
            {
                Quantity = g.Sum(l => l.Quantity),
                Amount = g.Sum(l => l.Amount),
                Vat = g.Sum(l => l.Vat),
                Discount = g.Sum(l => l.Discount),
            })
            .ToList();

    private static ReceiptBuilder WriteBusiness(ReceiptBuilder b, BusinessInfo business, SlipStyle style)
    {
        b.Logo(style.Logo).Text(business.Name, TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight);
        if (!string.IsNullOrWhiteSpace(business.Address)) b.Text(business.Address, TextStyle.Center);
        if (!string.IsNullOrWhiteSpace(business.Phone)) b.Text(business.Phone, TextStyle.Center);
        return b;
    }

    private static ReceiptBuilder WriteBill(ReceiptBuilder b, BillSlip d, SlipStyle style)
    {
        WriteBusiness(b, d.Business, style);
        b.Separator().Text($"BILL {d.BillNumber} · Ticket #{d.TicketNumber}", TextStyle.Center | TextStyle.Bold);
        if (d.Corrected) b.Text("Corrected bill · replaces earlier bills", TextStyle.Center | TextStyle.Bold);
        else if (d.BillNumber > 1) b.Text("Replaces earlier bills", TextStyle.Center);
        if (d.Reprint) b.Text("REPRINT", TextStyle.Center | TextStyle.Bold);
        b.Separator()
            .Columns2(d.Where, d.At.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture))
            .Columns2($"Waiter: {d.Waiter}", d.Terminal)
            .Separator();
        var lines = WriteItems(b, d.Lines, d.DiscountLabel, style);
        return WriteVat(b, lines, style);
    }

    /// <summary>The items as on the bill (gifts marked GIFT), then Subtotal and the discount if any, then TOTAL.</summary>
    private static IReadOnlyList<SlipLine> WriteItems(ReceiptBuilder b, IReadOnlyList<SlipLine> items, string? discountLabel, SlipStyle style)
    {
        var lines = AddUp(items);
        foreach (var line in lines)
            b.Columns2(line.IsGift ? LineText(line) + " GIFT" : LineText(line), Amount(line.Amount));
        b.Separator();
        var discount = lines.Sum(l => l.Discount);
        if (discount != 0)
            b.Columns2("Subtotal", Money(lines.Sum(l => l.Amount), style.Currency))
                .Columns2(discountLabel ?? "Discount", "-" + Money(discount, style.Currency));
        b.Columns2("TOTAL", Money(lines.Sum(l => l.Amount - l.Discount), style.Currency), TextStyle.Bold | TextStyle.DoubleHeight);
        return lines;
    }

    private static ReceiptBuilder WriteVat(ReceiptBuilder b, IReadOnlyList<SlipLine> lines, SlipStyle style)
    {

        // One line per VAT rate (0% included: zero-rated), from the VAT stored on each line, which is
        // worked out after the discount. Gifts carry none. The amount is never split over two lines:
        // on narrow paper the label is shortened.
        foreach (var rate in lines.Where(l => !l.VatExempt && !l.IsGift).GroupBy(l => l.VatRate).OrderByDescending(g => g.Key))
        {
            var vat = MoneyCents(rate.Sum(l => l.Vat), style.Currency);
            var full = $"Total includes VAT {Qty(rate.Key)}%: {vat}";
            if (full.Length <= style.Columns) b.Text(full);
            else b.Columns2($"Includes VAT {Qty(rate.Key)}%", vat);
        }
        if (lines.Any(l => l.VatExempt && !l.IsGift))
            b.Columns2("VAT exempt:", MoneyCents(lines.Where(l => l.VatExempt).Sum(l => l.Amount - l.Discount), style.Currency));
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

    /// <summary>
    /// To the dispense printer that printed the item: "VOID" in large text with the dispense header,
    /// the item as on the dispense slip, the reason, and who asked and approved.
    /// </summary>
    public static PrintDocument Void(VoidSlip d, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut)
            .Text("VOID", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Text(d.Header.ToUpperInvariant(), TextStyle.Center | TextStyle.Bold)
            .Columns2($"{d.Where} · #{d.TicketNumber}", d.At.ToString("HH:mm", CultureInfo.InvariantCulture))
            .Separator();
        foreach (var line in d.Lines)
        {
            b.Text($"{Qty(line.Quantity)} x {line.ShortName ?? line.Name}", TextStyle.Bold | TextStyle.DoubleHeight);
            if (line.Options is { Length: > 0 } options) b.Text("   " + options.ToUpperInvariant());
        }
        b.Separator()
            .Text($"Reason: {d.Reason}")
            .Text($"By: {d.RequestedBy} · Approved: {d.ApprovedBy}");
        return b.Cut().Build();
    }

    /// <summary>
    /// The final receipt: "RECEIPT No. 2041 · Ticket #1042" (COPY on a reprint), the items as on the
    /// bill, TOTAL and the rounding, each payment, the VAT lines, who served and who brought the
    /// money, then the footer. The cash drawer opens first when asked to.
    /// </summary>
    public static PrintDocument Receipt(ReceiptSlip d, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut);
        if (d.OpenDrawer) b.OpenDrawer();
        WriteBusiness(b, d.Business, style);
        b.Separator().Text($"RECEIPT No. {d.ReceiptNumber} · Ticket #{d.TicketNumber}", TextStyle.Center | TextStyle.Bold);
        if (d.Copy) b.Text("COPY", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight);
        b.Separator()
            .Columns2(d.Where, d.At.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture))
            .Columns2($"Waiter: {d.Waiter}", d.Terminal)
            .Text($"Cashier: {d.Cashier}")
            .Separator();

        var lines = WriteItems(b, d.Lines, d.DiscountLabel, style);
        if (d.Rounding != 0)
        {
            var due = lines.Sum(l => l.Amount - l.Discount) + d.Rounding;
            b.Columns2("Rounding", (d.Rounding > 0 ? "+" : "-") + Amount(Math.Abs(d.Rounding)))
                .Columns2("Amount due", Money(due, style.Currency), TextStyle.Bold);
        }
        b.Separator();
        foreach (var p in d.Payments)
        {
            var label = p.Type switch
            {
                "Cash" when p.Change is > 0 => $"Cash {Amount(p.Tendered ?? p.Amount)} · Change {Amount(p.Change.Value)}",
                "M-Pesa" => $"M-Pesa {p.Reference}",
                "Card" when !string.IsNullOrWhiteSpace(p.Reference) => $"Card · ref {p.Reference}",
                _ => p.Type,
            };
            b.Columns2(label, Amount(p.Amount));
        }
        b.Separator();
        WriteVat(b, lines, style);
        b.Separator().Text($"Served by {d.Waiter}");
        // "Cash brought by edna", once per type and person.
        foreach (var brought in d.Payments.Select(p => (p.Type, p.BroughtBy)).Distinct())
            b.Text($"{brought.Type} brought by {brought.BroughtBy}");
        return WriteFooter(b, d.Business, style).Cut().Build();
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
