using ClosedXML.Excel;

namespace Tausi.Application.Reports;

/// <summary>
/// A real .xlsx with one sheet: the title and filters in the top rows, then each table. Group
/// headers and subtotal rows are bold, numbers are numbers (money with a KSh format), dates are
/// dates, the first table's header row is frozen and columns fit their content.
/// </summary>
public static class ReportExcel
{
    private const string MoneyFormat = "\"KSh\" #,##0.00;[Red]-\"KSh\" #,##0.00";

    public static byte[] Build(ReportResult result, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Utc;
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add(SheetName(result.Title));
        var row = 1;
        sheet.Cell(row, 1).Value = result.Title;
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Cell(row, 1).Style.Font.FontSize = 14;
        row++;
        sheet.Cell(row++, 1).Value = $"Period: {result.Period}";
        foreach (var filter in result.FilterText) sheet.Cell(row++, 1).Value = filter;
        sheet.Cell(row++, 1).Value = $"Run {TimeZoneInfo.ConvertTime(result.RunAt, zone):d MMM yyyy HH:mm}";
        foreach (var note in result.Notes) sheet.Cell(row++, 1).Value = note;
        row++;

        var widest = 1;
        var frozen = false;
        foreach (var table in result.Tables)
        {
            widest = Math.Max(widest, table.Columns.Count);
            if (table.Title is not null)
            {
                sheet.Cell(row, 1).Value = table.Title;
                sheet.Cell(row, 1).Style.Font.Bold = true;
                sheet.Cell(row, 1).Style.Font.FontSize = 12;
                row++;
            }
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var cell = sheet.Cell(row, c + 1);
                cell.Value = table.Columns[c].Label;
                cell.Style.Font.Bold = true;
                cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                if (table.Columns[c].RightAligned) cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }
            if (!frozen)
            {
                sheet.SheetView.FreezeRows(row);
                frozen = true;
            }
            row++;

            foreach (var group in table.Groups)
            {
                if (group.Title is not null)
                {
                    var range = sheet.Range(row, 1, row, table.Columns.Count);
                    range.Style.Font.Bold = true;
                    range.Style.Fill.BackgroundColor = XLColor.FromHtml("#EDEDEA");
                    sheet.Cell(row, 1).Value = group.Title;
                    if (group.Subtitle is not null && table.Columns.Count > 2) sheet.Cell(row, 2).Value = group.Subtitle;
                    if (group.Total is { } total)
                    {
                        var last = sheet.Cell(row, table.Columns.Count);
                        last.Value = total;
                        last.Style.NumberFormat.Format = MoneyFormat;
                    }
                    row++;
                }
                foreach (var r in group.Rows) WriteRow(sheet, row++, table.Columns, r, bold: false, zone);
                if (group.Subtotal is { } subtotal) WriteRow(sheet, row++, table.Columns, subtotal, bold: true, zone);
                foreach (var line in group.Footer ?? [])
                {
                    sheet.Cell(row, 1).Value = line;
                    sheet.Cell(row++, 1).Style.Font.Italic = true;
                }
            }
            if (table.Totals is { } totals)
            {
                WriteRow(sheet, row, table.Columns, totals, bold: true, zone);
                sheet.Range(row, 1, row, table.Columns.Count).Style.Border.TopBorder = XLBorderStyleValues.Double;
                row++;
            }
            foreach (var line in table.Footer ?? []) sheet.Cell(row++, 1).Value = line;
            row++;
        }

        // Fit the columns to the table cells only (the title rows run across).
        for (var c = 1; c <= widest; c++)
        {
            var column = sheet.Column(c);
            column.AdjustToContents(result.Notes.Count + result.FilterText.Count + 5, row);
            if (column.Width > 60)
            {
                column.Width = 60;
                column.Style.Alignment.WrapText = true;
            }
        }
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteRow(IXLWorksheet sheet, int row, IReadOnlyList<ReportColumn> columns, ReportRow r, bool bold, TimeZoneInfo zone)
    {
        for (var c = 0; c < columns.Count && c < r.Cells.Count; c++)
        {
            var cell = sheet.Cell(row, c + 1);
            var type = columns[c].Type;
            switch (r.Cells[c])
            {
                case null:
                    break;
                case decimal d:
                    cell.Value = type == ColumnType.Percent ? d / 100m : d;
                    cell.Style.NumberFormat.Format = type switch
                    {
                        ColumnType.Money => MoneyFormat,
                        ColumnType.Percent => "0.0%",
                        _ => "#,##0.###",
                    };
                    break;
                case int i:
                    cell.Value = i;
                    break;
                case long l:
                    cell.Value = l;
                    cell.Style.NumberFormat.Format = type == ColumnType.Ticket ? "\"#\"0" : "#,##0";
                    break;
                case DateOnly date:
                    cell.Value = date.ToDateTime(TimeOnly.MinValue);
                    cell.Style.DateFormat.Format = "d mmm yyyy";
                    break;
                case DateTimeOffset at:
                    cell.Value = TimeZoneInfo.ConvertTime(at, zone).DateTime;
                    cell.Style.DateFormat.Format = type == ColumnType.Time ? "hh:mm" : "d mmm yyyy hh:mm";
                    break;
                case bool b:
                    cell.Value = b ? "Yes" : "No";
                    break;
                default:
                    cell.Value = Convert.ToString(r.Cells[c], System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
            if (columns[c].RightAligned) cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            if (bold) cell.Style.Font.Bold = true;
        }
    }

    /// <summary>Sheet names: at most 31 characters, none of : \ / ? * [ ].</summary>
    private static string SheetName(string title)
    {
        var clean = new string(title.Where(c => !":\\/?*[]".Contains(c)).ToArray());
        return clean.Length > 31 ? clean[..31] : clean.Length == 0 ? "Report" : clean;
    }
}
