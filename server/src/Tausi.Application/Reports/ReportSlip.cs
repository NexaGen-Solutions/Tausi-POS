using Tausi.Application.Printing;

namespace Tausi.Application.Reports;

/// <summary>
/// A short report on a receipt printer (42 or 32 columns): the title, period and filters, then
/// each group's header and total, its rows as "text … amount" (other figures underneath, indented),
/// its subtotal and footer lines, and the totals. Long names wrap; nothing runs past the paper.
/// </summary>
public static class ReportSlip
{
    public static PrintDocument Build(ReportResult result, string business, TimeZoneInfo zone, int columns, bool cut = true)
    {
        var b = new ReceiptBuilder(columns, cut)
            .Text(business, TextStyle.Center | TextStyle.Bold)
            .Text(result.Title.ToUpperInvariant(), TextStyle.Center | TextStyle.Bold)
            .Text(result.Period, TextStyle.Center);
        foreach (var filter in result.FilterText) b.Text(filter, TextStyle.Center);
        b.Text($"Run {TimeZoneInfo.ConvertTime(result.RunAt, zone):d MMM yyyy HH:mm}", TextStyle.Center);
        foreach (var note in result.Notes) b.Text(note);
        b.Separator();

        foreach (var table in result.Tables)
        {
            if (table.Title is not null) b.Text(table.Title.ToUpperInvariant(), TextStyle.Bold);
            foreach (var group in table.Groups)
            {
                if (group.Title is not null)
                    b.Columns2(group.Title + (group.Subtitle is { } s ? $" ({s})" : ""),
                        group.Total is { } t ? ReportFormat.Money(t) : "", TextStyle.Bold);
                foreach (var row in group.Rows) Row(b, table, row, zone, TextStyle.None);
                if (group.Subtotal is { } subtotal) Row(b, table, subtotal, zone, TextStyle.Bold);
                foreach (var line in group.Footer ?? []) b.Text(line);
                if (group.Title is not null) b.Separator('.');
            }
            if (table.Totals is { } totals)
            {
                b.Separator();
                Row(b, table, totals, zone, TextStyle.Bold);
            }
            foreach (var line in table.Footer ?? []) b.Text(line);
            b.Separator();
        }
        if (result.IsEmpty) b.Text("Nothing to show.");
        return b.Cut().Build();
    }

    /// <summary>
    /// A row with one figure (and maybe a quantity): its words on the left, the figure on the right
    /// ("2 x Tusker Lager … 500"). A row with more figures: its words, then each figure on a line of
    /// its own with its label, so nothing is mistaken for something else.
    /// </summary>
    private static void Row(ReceiptBuilder b, ReportTable table, ReportRow row, TimeZoneInfo zone, TextStyle style)
    {
        string Cell(int i) => ReportFormat.Text(i < row.Cells.Count ? row.Cells[i] : null, table.Columns[i].Type, zone);
        var columns = table.Columns.Select((c, i) => (c, i)).ToList();
        var quantity = columns.Where(x => x.c.Type == ColumnType.Quantity).Select(x => x.i).DefaultIfEmpty(-1).First();
        var figures = columns.Where(x => x.c.RightAligned && x.i != quantity).Select(x => x.i).ToList();
        var words = string.Join(" ", columns.Where(x => !x.c.RightAligned).Select(x => Cell(x.i)).Where(t => t.Length > 0));
        if (figures.Count <= 1)
        {
            var left = quantity >= 0 && Cell(quantity) is { Length: > 0 } q ? $"{q} x {words}" : words;
            b.Columns2(left, figures.Count == 1 ? Cell(figures[0]) : "", style);
            return;
        }
        b.Text(words, style | TextStyle.Bold);
        foreach (var i in columns.Where(x => x.c.RightAligned).Select(x => x.i))
            if (Cell(i) is { Length: > 0 } value) b.Columns2("  " + table.Columns[i].Label, value, style);
    }
}
