using Tausi.Application.Printing;

namespace Tausi.Application.Reports;

/// <summary>
/// A report on A4: a header with the business name, the title, the period and filters and when it
/// was run; then each table with its column headers repeated on every page, grey group headers,
/// bold subtotals and totals, and "Page n of N" at the foot. Text wraps and is never cut; number
/// columns keep their full width. Landscape when a table has more than six columns.
/// </summary>
public static class ReportPdf
{
    private const float Margin = 36;
    private const float Size = 8.5f;
    private const float LineHeight = 11;
    private const float Pad = 3;
    private const float FooterSpace = 30;

    public static byte[] Build(ReportResult result, string business, TimeZoneInfo zone)
    {
        var landscape = result.Tables.Any(t => t.Columns.Count > 6);
        var pdf = new SimplePdf(landscape);
        var writer = new Writer(pdf, result, business, zone);
        writer.Header();
        foreach (var note in result.Notes) writer.Paragraph(note, bold: false);
        foreach (var table in result.Tables) writer.Table(table);
        if (result.IsEmpty) writer.Paragraph("Nothing to show for these filters.", bold: false);

        for (var i = 0; i < pdf.PageCount; i++)
        {
            pdf.UsePage(i);
            var text = $"Page {i + 1} of {pdf.PageCount}";
            pdf.Text(pdf.PaperWidth - Margin - SimplePdf.Measure(text, 8), pdf.PaperHeight - 18, text, 8);
            pdf.Text(Margin, pdf.PaperHeight - 18, $"{business} · {result.Title}", 8);
        }
        return pdf.Build();
    }

    /// <summary>Splits text into lines that fit <paramref name="width"/>: at spaces, or inside a word that is too long on its own.</summary>
    public static List<string> Wrap(string text, float width, float size, bool bold)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (SimplePdf.Measure(candidate, size, bold) <= width)
                {
                    line = candidate;
                    continue;
                }
                if (line.Length > 0) lines.Add(line);
                line = word;
                // A word wider than the column on its own is broken where it must be.
                while (SimplePdf.Measure(line, size, bold) > width && line.Length > 1)
                {
                    var fit = 1;
                    while (fit < line.Length && SimplePdf.Measure(line[..(fit + 1)], size, bold) <= width) fit++;
                    lines.Add(line[..fit]);
                    line = line[fit..];
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    private sealed class Writer(SimplePdf pdf, ReportResult result, string business, TimeZoneInfo zone)
    {
        private float _y = Margin;
        private float Bottom => pdf.PaperHeight - Margin - FooterSpace;
        private float Usable => pdf.PaperWidth - 2 * Margin;

        public void Header()
        {
            pdf.Text(Margin, _y + 9, business, 9, bold: true);
            var run = $"Run {TimeZoneInfo.ConvertTime(result.RunAt, zone):d MMM yyyy HH:mm}";
            pdf.Text(pdf.PaperWidth - Margin - SimplePdf.Measure(run, 9), _y + 9, run, 9);
            _y += 28;
            pdf.Text(Margin, _y, result.Title, 15, bold: true);
            _y += 8;
            Paragraph(string.Join(" · ", new[] { result.Period }.Concat(result.FilterText)), bold: false);
            _y += 4;
        }

        public void Paragraph(string text, bool bold)
        {
            foreach (var line in Wrap(text, Usable, 9, bold))
            {
                NeedRoom(LineHeight + 2, null);
                _y += LineHeight + 1;
                pdf.Text(Margin, _y, line, 9, bold);
            }
        }

        public void Table(ReportTable table)
        {
            var widths = ColumnWidths(table);
            if (table.Title is not null)
            {
                NeedRoom(60, null);
                _y += 18;
                pdf.Text(Margin, _y, table.Title, 11, bold: true);
                _y += 4;
            }
            NeedRoom(40, null);
            ColumnHeader(table, widths);
            foreach (var group in table.Groups)
            {
                if (group.Title is not null) GroupHeader(table, widths, group);
                foreach (var row in group.Rows) Row(table, widths, row, bold: false);
                if (group.Subtotal is { } subtotal) Row(table, widths, subtotal, bold: true, ruleAbove: true);
                foreach (var line in group.Footer ?? []) FooterLine(table, widths, line);
            }
            if (table.Totals is { } totals) Row(table, widths, totals, bold: true, ruleAbove: true, double_: true);
            foreach (var line in table.Footer ?? []) FooterLine(table, widths, line);
            _y += 6;
        }

        private float[] ColumnWidths(ReportTable table)
        {
            var natural = table.Columns.Select((c, i) =>
            {
                var widest = SimplePdf.Measure(c.Label.ToUpperInvariant(), 7, bold: true);
                foreach (var group in table.Groups)
                foreach (var row in group.Rows.Take(2000).Concat(group.Subtotal is { } s ? [s] : []))
                    if (i < row.Cells.Count)
                        widest = Math.Max(widest, SimplePdf.Measure(ReportFormat.Text(row.Cells[i], c.Type, zone), Size, bold: false));
                if (table.Totals is { } t && i < t.Cells.Count)
                    widest = Math.Max(widest, SimplePdf.Measure(ReportFormat.Text(t.Cells[i], c.Type, zone), Size, bold: true));
                return widest + 2 * Pad + 2;
            }).ToArray();
            var fixedColumns = table.Columns.Select(c => c.Type != ColumnType.Text).ToArray();
            if (natural.Sum() <= Usable)
            {
                // Room to spare: the text columns share it, so the table runs across the page.
                var text = natural.Where((_, i) => !fixedColumns[i]).Sum();
                if (text == 0) return natural;
                var spare = Usable - natural.Sum();
                return natural.Select((w, i) => fixedColumns[i] ? w : w + spare * w / text).ToArray();
            }

            // Number, date and ticket columns keep their width; text columns share the rest and wrap.
            var fixedWidth = natural.Where((_, i) => fixedColumns[i]).Sum();
            var textNatural = natural.Where((_, i) => !fixedColumns[i]).Sum();
            var left = Math.Max(Usable - fixedWidth, 60 * fixedColumns.Count(f => !f));
            return natural.Select((w, i) => fixedColumns[i] ? w : Math.Max(50, w / textNatural * left)).ToArray();
        }

        private void ColumnHeader(ReportTable table, float[] widths)
        {
            var x = Margin;
            _y += 14;
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var label = table.Columns[i].Label.ToUpperInvariant();
                var lines = Wrap(label, widths[i] - 2 * Pad, 7, bold: true);
                for (var l = 0; l < lines.Count; l++)
                    Cell(x, widths[i], _y + l * 9, lines[l], 7, bold: true, right: table.Columns[i].RightAligned);
                x += widths[i];
            }
            var height = Math.Max(1, table.Columns.Select((c, i) => Wrap(c.Label.ToUpperInvariant(), widths[i] - 2 * Pad, 7, true).Count).Max());
            _y += (height - 1) * 9 + 4;
            pdf.Line(Margin, _y, Margin + widths.Sum(), _y, 0.75f);
        }

        private void GroupHeader(ReportTable table, float[] widths, ReportGroup group)
        {
            var total = group.Total is { } t ? ReportFormat.Money(t) : null;
            var titleWidth = Math.Max(widths[0], widths.Sum() / 2) - 2 * Pad;
            var lines = Wrap(group.Title + (group.Subtitle is { } s ? $" · {s}" : ""), titleWidth, Size, bold: true);
            var height = lines.Count * LineHeight + 2 * Pad + 2;
            NeedRoom(height + LineHeight * 2, table, widths);
            pdf.Fill(Margin, _y + 1, widths.Sum(), height, 0.92f);
            for (var l = 0; l < lines.Count; l++) pdf.Text(Margin + Pad, _y + Pad + 9 + l * LineHeight, lines[l], Size, bold: true);
            if (total is not null)
                pdf.Text(Margin + widths.Sum() - Pad - SimplePdf.Measure(total, Size, true), _y + Pad + 9, total, Size, bold: true);
            _y += height;
        }

        private void Row(ReportTable table, float[] widths, ReportRow row, bool bold, bool ruleAbove = false, bool double_ = false)
        {
            var cells = table.Columns.Select((c, i) =>
                Wrap(ReportFormat.Text(i < row.Cells.Count ? row.Cells[i] : null, c.Type, zone), widths[i] - 2 * Pad, Size, bold)).ToList();
            var height = cells.Max(c => c.Count) * LineHeight + 2 * Pad;
            NeedRoom(height, table, widths);
            if (ruleAbove)
            {
                pdf.Line(Margin, _y + 1, Margin + widths.Sum(), _y + 1, 0.5f);
                if (double_) pdf.Line(Margin, _y + 3, Margin + widths.Sum(), _y + 3, 0.5f);
            }
            var x = Margin;
            for (var i = 0; i < cells.Count; i++)
            {
                for (var l = 0; l < cells[i].Count; l++)
                    Cell(x, widths[i], _y + Pad + 9 + l * LineHeight, cells[i][l], Size, bold, table.Columns[i].RightAligned);
                x += widths[i];
            }
            _y += height;
            if (!bold) pdf.Line(Margin, _y, Margin + widths.Sum(), _y, 0.2f);
        }

        private void FooterLine(ReportTable table, float[] widths, string text)
        {
            foreach (var line in Wrap(text, widths.Sum() - 2 * Pad, 8, bold: false))
            {
                NeedRoom(LineHeight, table, widths);
                _y += LineHeight;
                pdf.Text(Margin + Pad, _y, line, 8);
            }
            _y += 3;
        }

        private void Cell(float x, float width, float y, string text, float size, bool bold, bool right)
        {
            var left = right ? x + width - Pad - SimplePdf.Measure(text, size, bold) : x + Pad;
            pdf.Text(left, y, text, size, bold);
        }

        /// <summary>Starts a new page when <paramref name="height"/> doesn't fit, repeating the table's column headers.</summary>
        private void NeedRoom(float height, ReportTable? table, float[]? widths = null)
        {
            if (_y + height <= Bottom) return;
            pdf.NewPage();
            _y = Margin;
            if (table is not null && widths is not null) ColumnHeader(table, widths);
        }
    }
}
