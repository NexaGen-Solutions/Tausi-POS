using System.Globalization;
using Tausi.Application.Printing;

namespace Tausi.Application.Stock;

/// <summary>Stock slips for the receipt printer: a transfer slip and the blind count sheet.</summary>
public static class StockSlips
{
    private static string Time(DateTimeOffset at) => at.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"TRANSFER · Store → Bar" with the lines, for whoever carries the stock.</summary>
    public static PrintDocument Transfer(
        string from, string to, DateTimeOffset at, string by, IReadOnlyList<(string Item, string Quantity)> lines, bool pending, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut)
            .Text("TRANSFER", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Text($"{from} -> {to}", TextStyle.Center | TextStyle.Bold)
            .Text(Time(at), TextStyle.Center)
            .Separator();
        foreach (var (item, quantity) in lines) b.Columns2(item, quantity);
        b.Separator().Text($"By: {by}");
        if (pending) b.Text("Waiting for approval", TextStyle.Bold);
        b.Feed(2).Text(new string('_', Math.Min(style.Columns, 30))).Text("Received by");
        return b.Cut().Build();
    }

    /// <summary>The count sheet: every item with a blank per unit and no expected figures (a blind count).</summary>
    public static PrintDocument CountSheet(
        string location, DateTimeOffset asAt, IReadOnlyList<(string Item, IReadOnlyList<string> Units)> items, SlipStyle style)
    {
        var b = new ReceiptBuilder(style.Columns, style.Cut)
            .Text("STOCK COUNT", TextStyle.Center | TextStyle.Bold | TextStyle.DoubleHeight)
            .Text(location, TextStyle.Center | TextStyle.Bold)
            .Text($"As at {Time(asAt)}", TextStyle.Center)
            .Separator();
        foreach (var (item, units) in items)
        {
            b.Text(item, TextStyle.Bold);
            b.Text("  " + string.Join("  ", units.Select(u => $"{u} ____")));
        }
        b.Separator().Text("Counted by ____________");
        return b.Cut().Build();
    }
}
