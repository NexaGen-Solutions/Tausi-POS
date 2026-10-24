using System.Globalization;
using Tausi.Application.Printing;
using Tausi.Domain.Cash;

namespace Tausi.Application.Cash;

/// <summary>
/// The cash slips, on the shared builder (42 or 32 columns), with times in the business's zone:
/// the cash-up, the pay-out and drop slips, the waiter's slip and the day report.
/// </summary>
public static class CashSlips
{
    private static string Amount(decimal amount) => Slips.Amount(amount);

    private static string Signed(decimal amount) => amount == 0 ? "0" : (amount > 0 ? "+" : "-") + Amount(Math.Abs(amount));

    private static string Time(DateTimeOffset at) => at.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// "CASH-UP · Counter A · CAROL" with the shift's times; float, cash settled, pay-outs (by
    /// category), drops and expected; the count by note, counted and the difference; M-Pesa and
    /// card; cash brought by each waiter; the receipts; and the sign-off with a signature line.
    /// </summary>
    public static PrintDocument CashUp(ShiftDto s, string business, Func<DateTimeOffset, DateTimeOffset> local, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut)
            .Text(business, TextStyle.Center | TextStyle.Bold)
            .Text($"CASH-UP · {s.Terminal} · {s.Cashier}", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Text($"{Time(local(s.OpenedAt))} - {(s.ClosedAt is { } c ? Time(local(c)) : "open")}", TextStyle.Center)
            .Separator()
            .Columns2("Opening float", Amount(s.OpeningFloat))
            .Columns2("Cash settled", "+" + Amount(s.CashSettled));
        foreach (var payOut in s.Movements.Where(m => m.Kind == nameof(DrawerMovementKind.PayOut)))
            b.Columns2($"Pay out · {payOut.Category}", "-" + Amount(payOut.Amount));
        if (s.Drops > 0) b.Columns2("Dropped to safe", "-" + Amount(s.Drops));
        var expected = s.Expected ?? s.OpeningFloat + s.CashSettled - s.PayOuts - s.Drops;
        b.Columns2("EXPECTED", Slips.Money(expected, style.Currency), TextStyle.Bold).Separator();

        foreach (var note in s.Denominations)
        {
            var count = s.CountNotes.GetValueOrDefault(note.ToString(CultureInfo.InvariantCulture));
            if (count > 0) b.Columns2($"{Amount(note)} x {count}", Amount(note * count));
        }
        if (s.CountCoins > 0) b.Columns2("Coins", Amount(s.CountCoins));
        var difference = s.Difference ?? (s.Counted ?? 0) - expected;
        b.Columns2("COUNTED", Slips.Money(s.Counted ?? 0, style.Currency), TextStyle.Bold)
            .Columns2(difference < 0 ? "SHORT" : difference > 0 ? "OVER" : "Difference", Signed(difference), TextStyle.Bold)
            .Separator()
            .Columns2($"M-Pesa ({s.MpesaCount})", Amount(s.MpesaTotal));
        if (s.MpesaNoCodeCount > 0) b.Columns2($"M-Pesa no code ({s.MpesaNoCodeCount})", Amount(s.MpesaNoCodeTotal));
        b.Columns2($"Card ({s.CardCount})", Amount(s.CardTotal));
        if (s.Waiters.Count > 0)
        {
            b.Separator().Text("Cash brought by", TextStyle.Bold);
            foreach (var w in s.Waiters) b.Columns2(w.Name, Amount(w.Cash));
        }
        b.Separator().Columns2("Receipts", s.Receipts == 0 ? "0" : $"{s.Receipts} · No. {s.FirstReceipt}-{s.LastReceipt}");
        if (!string.IsNullOrWhiteSpace(s.Note)) b.Text($"Note: {s.Note}");
        b.Separator().Text($"Signed off by {s.SignedOffBy ?? "-"}").Feed(2).Text(new string('_', Math.Min(style.Columns, 30))).Text("Signature");
        return b.Cut().Build();
    }

    /// <summary>A small slip to keep with the cash: PAY OUT or CASH DROP, the amount, who, when and why.</summary>
    public static PrintDocument Movement(DrawerMovement m, string business, string terminal, string by, string? approvedBy,
        Func<DateTimeOffset, DateTimeOffset> local, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut)
            .Text(business, TextStyle.Center)
            .Text(m.Kind == DrawerMovementKind.PayOut ? "PAY OUT" : "CASH DROP", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Separator()
            .Columns2("Amount", Slips.Money(m.Amount, style.Currency), TextStyle.Bold | TextStyle.DoubleHeight);
        if (m.CategoryName is not null) b.Columns2("For", m.CategoryName);
        if (!string.IsNullOrWhiteSpace(m.Note)) b.Text($"Note: {m.Note}");
        b.Columns2("By", $"{by} · {terminal}")
            .Columns2("When", local(m.At).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture));
        if (approvedBy is not null) b.Columns2("Approved by", approvedBy);
        b.Feed(2).Text(new string('_', Math.Min(style.Columns, 30))).Text("Signature");
        return b.Cut().Build();
    }

    /// <summary>The waiter's slip: name and day, total sales, tickets, cash / M-Pesa / card brought, and open bills.</summary>
    public static PrintDocument Waiter(MySalesDto m, string business, Func<DateTimeOffset, DateTimeOffset> local, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut)
            .Text(business, TextStyle.Center)
            .Text($"MY SALES · {m.Name}", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Text(m.PeriodOpenedAt is { } opened ? $"Day from {Time(local(opened))}" : "No day open", TextStyle.Center)
            .Separator()
            .Columns2("Total sales", Slips.Money(m.Total, style.Currency), TextStyle.Bold)
            .Columns2("Tickets", m.Tickets.ToString(CultureInfo.InvariantCulture))
            .Separator()
            .Text("Brought to the cashier", TextStyle.Bold)
            .Columns2("Cash", Amount(m.Cash))
            .Columns2("M-Pesa", Amount(m.Mpesa))
            .Columns2("Card", Amount(m.Card))
            .Separator()
            .Columns2("Bills still open", m.OpenBills.Count.ToString(CultureInfo.InvariantCulture), TextStyle.Bold);
        foreach (var bill in m.OpenBills) b.Columns2($"#{bill.Number} {bill.Where}", Amount(bill.Total));
        return b.Cut().Build();
    }

    /// <summary>"DAY REPORT · Z": the day's figures, the period's times and who closed it.</summary>
    public static PrintDocument DayReport(Day.DaySummaryDto d, string business, DateTimeOffset openedAt, DateTimeOffset closedAt,
        string closedBy, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut)
            .Text(business, TextStyle.Center | TextStyle.Bold)
            .Text("DAY REPORT · Z", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Text($"{Time(openedAt)} - {Time(closedAt)}", TextStyle.Center)
            .Text($"Closed by {closedBy}", TextStyle.Center)
            .Separator()
            .Columns2("Tickets", d.Tickets.ToString(CultureInfo.InvariantCulture))
            .Columns2("Gross sales", Amount(d.Gross));
        if (d.Discounts != 0) b.Columns2("Discounts", "-" + Amount(d.Discounts));
        b.Columns2("NET + VAT", Slips.Money(d.Net + d.Vat, style.Currency), TextStyle.Bold)
            .Columns2("Gifts (at price)", Amount(d.Gifts))
            .Columns2("Voids", Amount(d.Voids))
            .Separator();
        foreach (var rate in d.VatRates)
            b.Columns2($"{rate.Label} net", Amount(rate.Net)).Columns2($"{rate.Label} VAT", Slips.MoneyCents(rate.Vat, style.Currency));
        b.Separator().Text("Payments", TextStyle.Bold);
        foreach (var p in d.Payments) b.Columns2($"{p.Type} ({p.Count})", Amount(p.Amount));
        if (d.Rounding != 0) b.Columns2("Rounding", Signed(d.Rounding));
        b.Columns2("TOTAL TAKEN", Slips.Money(d.PaymentsTotal, style.Currency), TextStyle.Bold).Separator();
        if (d.ShiftDifferences.Count > 0)
        {
            b.Text("Cash-ups", TextStyle.Bold);
            foreach (var s in d.ShiftDifferences) b.Columns2($"{s.Cashier} · {s.Terminal}", Signed(s.Difference));
        }
        b.Columns2("Pay-outs", Amount(d.PayOuts));
        if (d.Profit is { } profit)
        {
            // The day's profit from the books (late entries made after the close aren't on it).
            b.Separator().Text("Profit", TextStyle.Bold)
                .Columns2("Net sales", Amount(profit.NetSales))
                .Columns2("Cost of sales", "-" + Amount(profit.CostOfSales))
                .Columns2("Gross profit", Amount(profit.GrossProfit))
                .Columns2("Expenses", "-" + Amount(profit.Expenses))
                .Columns2("Stock loss, cash diff.", Signed(-profit.StockLossAndCash));
            if (profit.BankCharges != 0) b.Columns2("Bank charges", "-" + Amount(profit.BankCharges));
            if (profit.OtherIncome != 0) b.Columns2("Other income", Amount(profit.OtherIncome));
            b.Columns2("NET FOR THE DAY", Slips.Money(profit.Net, style.Currency), TextStyle.Bold);
            if (profit.NoRecipeItems.Count > 0) b.Text($"Cost of sales missing for {profit.NoRecipeItems.Count} items with no recipe");
        }
        return b.Cut().Build();
    }
}
