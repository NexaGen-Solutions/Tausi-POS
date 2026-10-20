using System.Globalization;
using System.Text;

namespace Nexagen.Application.Menu;

/// <summary>
/// Minimal CSV (RFC 4180: quotes, commas or semicolons, CRLF or LF). Files from SambaPOS may be
/// UTF-8 or Windows-1252: valid UTF-8 is read as UTF-8, anything else as Windows-1252.
/// </summary>
public static class MenuCsv
{
    static MenuCsv() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    public static List<List<string>> Parse(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(f => f.Length > 0)) rows.Add(row);
                row = [];
            }
            else field.Append(c);
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Length > 0)) rows.Add(row);
        return rows;
    }

    public static string Write(IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(string.Join(',', row.Select(Escape))).Append("\r\n");
        return sb.ToString();
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r', ';']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    /// <summary>"1,200.00", "1200", "KSh 250" all parse; empty is null.</summary>
    public static bool TryMoney(string text, out decimal? value)
    {
        value = null;
        var cleaned = new string(text.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        if (text.Trim().Length == 0) return true;
        if (cleaned.Length == 0 || !decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return false;
        value = d;
        return true;
    }

    public static string Money(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>yes/no, true/false, 1/0, active/inactive; empty is null.</summary>
    public static bool TryBool(string text, out bool? value)
    {
        value = null;
        switch (text.Trim().ToLowerInvariant())
        {
            case "":
                return true;
            case "yes" or "y" or "true" or "1" or "active":
                value = true;
                return true;
            case "no" or "n" or "false" or "0" or "inactive":
                value = false;
                return true;
            default:
                return false;
        }
    }
}
