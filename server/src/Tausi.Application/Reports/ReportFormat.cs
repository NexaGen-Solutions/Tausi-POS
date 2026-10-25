using System.Globalization;
using System.Text;

namespace Tausi.Application.Reports;

/// <summary>How report values read as text (PDF, receipt printer, CSV): the same rules as the screen.</summary>
public static class ReportFormat
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>For people: "1,370", "1,370.50", "62.5%", "#1042", "24 Oct 14:05".</summary>
    public static string Text(object? value, ColumnType type, TimeZoneInfo zone) => value switch
    {
        null => "",
        string s => s,
        decimal d when type == ColumnType.Percent => d.ToString("0.#", Invariant) + "%",
        decimal d when type == ColumnType.Quantity => d.ToString("#,##0.###", Invariant),
        decimal d => d.ToString("#,##0.##", Invariant),
        int i => i.ToString("#,##0", Invariant),
        long l when type == ColumnType.Ticket => "#" + l.ToString(Invariant),
        long l => l.ToString("#,##0", Invariant),
        DateOnly date => date.ToString("ddd d MMM yyyy", Invariant),
        DateTimeOffset at when type == ColumnType.Time => TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", Invariant),
        DateTimeOffset at => TimeZoneInfo.ConvertTime(at, zone).ToString("d MMM HH:mm", Invariant),
        bool b => b ? "Yes" : "No",
        _ => Convert.ToString(value, Invariant) ?? "",
    };

    /// <summary>For files: plain numbers (no thousands separators), ISO dates, local times.</summary>
    public static string Raw(object? value, ColumnType type, TimeZoneInfo zone) => value switch
    {
        null => "",
        string s => s,
        decimal d => d.ToString("0.##", Invariant),
        int i => i.ToString(Invariant),
        long l => l.ToString(Invariant),
        DateOnly date => date.ToString("yyyy-MM-dd", Invariant),
        DateTimeOffset at => TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", Invariant),
        bool b => b ? "yes" : "no",
        _ => Convert.ToString(value, Invariant) ?? "",
    };

    public static string Money(decimal amount) => amount.ToString("#,##0.##", Invariant);
}

/// <summary>
/// A flat CSV: no group, subtotal or total rows; the group (and the table, when a report has
/// several) repeated in its own column on every row.
/// </summary>
public static class ReportCsv
{
    public static byte[] Build(ReportResult result, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Utc;
        var sb = new StringBuilder();
        var manyTables = result.Tables.Count > 1;
        foreach (var table in result.Tables)
        {
            var grouped = table.Groups.Any(g => g.Title is not null);
            var header = new List<string>();
            if (manyTables) header.Add("Table");
            if (grouped) header.Add("Group");
            header.AddRange(table.Columns.Select(c => c.Label));
            sb.AppendLine(string.Join(",", header.Select(Quote)));
            foreach (var group in table.Groups)
            foreach (var row in group.Rows)
            {
                var cells = new List<string>();
                if (manyTables) cells.Add(table.Title ?? "");
                if (grouped) cells.Add(group.Title ?? "");
                cells.AddRange(table.Columns.Select((c, i) => ReportFormat.Raw(i < row.Cells.Count ? row.Cells[i] : null, c.Type, zone)));
                sb.AppendLine(string.Join(",", cells.Select(Quote)));
            }
            if (manyTables) sb.AppendLine();
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Quote(string value)
    {
        // A leading =, +, - or @ would run as a formula in a spreadsheet.
        if (value.Length > 0 && "=+@".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
