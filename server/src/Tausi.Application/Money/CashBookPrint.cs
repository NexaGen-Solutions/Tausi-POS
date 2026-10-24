using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Domain.Common;
using Tausi.Domain.Printing;

namespace Tausi.Application.Money;

public sealed record PrintSummaryRequest(Guid PrinterId);

/// <summary>The daily summary on paper: an A4 PDF, or on a receipt printer.</summary>
public sealed class CashBookPrint(ITausiDb db, ICurrentSession session, MoneyAccess access, PrintQueue queue)
{
    private static string Amount(decimal amount) => amount.ToString("#,##0.##", CultureInfo.InvariantCulture);

    /// <summary>The summary's lines in order: (label, amount, bold). Expenses and losses by part, indented.</summary>
    public static IReadOnlyList<(string Label, decimal Amount, bool Bold)> Figures(DailySummaryDto s)
    {
        var lines = new List<(string, decimal, bool)>
        {
            ("Sales (incl. VAT)", s.SalesInclVat, false),
            ("VAT", s.Vat, false),
        };
        if (s.Rounding != 0) lines.Add(("Rounding", s.Rounding, false));
        lines.AddRange(new (string, decimal, bool)[]
        {
            ("Net sales", s.NetSales, true),
            ("Cost of sales", s.CostOfSales, false),
            ($"Gross profit{(s.GrossMarginPercent is { } m ? $" ({m:0.#}%)" : "")}", s.GrossProfit, true),
            ("Expenses", s.Expenses, false),
        });
        lines.AddRange(s.ExpensesByCategory.Select(c => ($"  {c.Name}", c.Amount, false)));
        lines.Add(("Stock loss and cash differences", s.StockLossAndCash, false));
        lines.AddRange(s.StockLossParts.Select(c => ($"  {c.Name}", c.Amount, false)));
        lines.Add(("Bank charges", s.BankCharges, false));
        lines.Add(("Other income", s.OtherIncome, false));
        lines.Add(("Net for the day", s.Net, true));
        return lines;
    }

    /// <summary>The same lines with the amounts as printed text.</summary>
    public static IReadOnlyList<(string Label, string Amount, bool Bold)> Lines(DailySummaryDto s) =>
        Figures(s).Select(l => (l.Label, Amount(l.Amount), l.Bold)).ToList();

    public async Task<byte[]> Pdf(DateOnly date, CancellationToken ct)
    {
        await access.RequireView(session.RequireUser(), ct);
        var summary = await Summaries.For(db, date, includeLate: true, ct);
        var tiles = await CashBook.Tiles(db, date, ct);
        var business = (await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessName);
        var pdf = new SimplePdf();
        const float left = 50, right = SimplePdf.PageWidth - 50;
        pdf.Text(left, 60, $"Daily summary · {date:dddd d MMMM yyyy}", 18, bold: true);
        pdf.Text(left, 80, business, 11);
        var y = 115f;
        foreach (var (label, amount, bold) in Lines(summary))
        {
            pdf.Text(left, y, label, 11, bold);
            pdf.Text(right - SimplePdf.Width(amount, 11), y, amount, 11, bold);
            if (bold) pdf.Line(left, y + 6, right, y + 6, 0.25f);
            y += 20;
        }
        y += 15;
        pdf.Text(left, y, "At the end of the day", 12, bold: true);
        y += 20;
        foreach (var (label, amount) in new[]
                 {
                     ("Cash on hand", tiles.CashOnHand), ("M-Pesa received", tiles.MpesaReceived), ("M-Pesa balance", tiles.MpesaBalance),
                     ("Bank", tiles.Bank), ("Owed to suppliers", tiles.OwedToSuppliers),
                 })
        {
            pdf.Text(left, y, label, 11);
            pdf.Text(right - SimplePdf.Width(Amount(amount), 11), y, Amount(amount), 11);
            y += 20;
        }
        y += 10;
        pdf.Text(left, y, "Cost of sales comes from recipes and stock cost, so profit is real, not an estimate.", 9);
        if (summary.NoRecipeItems.Count > 0)
            pdf.Text(left, y + 16, $"Cost of sales is missing for {summary.NoRecipeItems.Count} items with no recipe: {string.Join(", ", summary.NoRecipeItems)}", 9);
        return pdf.Build();
    }

    public async Task PrintReceipt(DateOnly date, PrintSummaryRequest r, CancellationToken ct)
    {
        await access.RequireView(session.RequireUser(), ct);
        var printer = await db.Printers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PrinterId && p.Active, ct)
            ?? throw new DomainException("Choose a printer that is switched on.");
        var summary = await Summaries.For(db, date, includeLate: true, ct);
        var business = (await SettingsRegistry.Load(db, ct)).String(SettingsRegistry.BusinessName);
        var b = new ReceiptBuilder(printer.Columns, printer.CutAfterSlip)
            .Text(business, TextStyle.Center | TextStyle.Bold)
            .Text("DAILY SUMMARY", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Text(date.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture), TextStyle.Center)
            .Separator();
        foreach (var (label, amount, bold) in Lines(summary)) b.Columns2(label, amount, bold ? TextStyle.Bold : TextStyle.None);
        queue.Enqueue(printer, PrintJobKind.CashUp, b.Cut().Build(), null);
        await db.SaveChangesAsync(ct);
    }
}
