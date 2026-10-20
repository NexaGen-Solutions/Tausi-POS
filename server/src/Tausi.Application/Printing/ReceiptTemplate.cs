using System.Text.RegularExpressions;

namespace Tausi.Application.Printing;

/// <summary>
/// Simple text templates for slips. One instruction per line:
/// <code>
/// @logo                     the business logo (skipped when there is none)
/// @cut                      cut the paper
/// @feed 3                   blank lines
/// ---                       separator line (=== for a double one)
/// [center][bold][big]Text   styled text; tags can be combined, in any order
/// Left text || Right text   two columns, the right one aligned to the edge
/// </code>
/// <c>{{name}}</c> is replaced from the data. Values are inserted as plain text, so data cannot
/// add instructions.
/// </summary>
public static partial class ReceiptTemplate
{
    public static PrintDocument Render(
        string template,
        IReadOnlyDictionary<string, string> data,
        int columns,
        MonoBitmap? logo = null)
    {
        var builder = new ReceiptBuilder(columns);
        foreach (var rawLine in template.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            switch (line)
            {
                case "@logo":
                    builder.Logo(logo);
                    continue;
                case "@cut":
                    builder.Cut();
                    continue;
                case "---":
                    builder.Separator('-');
                    continue;
                case "===":
                    builder.Separator('=');
                    continue;
            }
            if (line.StartsWith("@feed"))
            {
                builder.Feed(int.TryParse(line[5..].Trim(), out var n) ? n : 1);
                continue;
            }

            var style = TextStyle.None;
            var match = TagPattern().Match(line);
            while (match.Success && match.Index == 0)
            {
                style |= match.Groups[1].Value switch
                {
                    "center" => TextStyle.Center,
                    "bold" => TextStyle.Bold,
                    "big" => TextStyle.DoubleHeight,
                    _ => TextStyle.None,
                };
                line = line[match.Length..];
                match = TagPattern().Match(line);
            }

            var split = line.IndexOf("||", StringComparison.Ordinal);
            if (split >= 0)
                builder.Columns2(Fill(line[..split].Trim(), data), Fill(line[(split + 2)..].Trim(), data), style);
            else
                builder.Text(Fill(line, data), style);
        }
        return builder.Build();
    }

    private static string Fill(string text, IReadOnlyDictionary<string, string> data) =>
        PlaceholderPattern().Replace(text, m => data.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    [GeneratedRegex(@"^\[(center|bold|big)\]")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    private static partial Regex PlaceholderPattern();
}
