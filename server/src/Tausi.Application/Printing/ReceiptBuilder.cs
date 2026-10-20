using System.Globalization;
using System.Text;

namespace Tausi.Application.Printing;

[Flags]
public enum TextStyle
{
    None = 0,
    Bold = 1,
    DoubleHeight = 2,
    Center = 4,
}

/// <summary>
/// Builds ESC/POS output and a plain-text preview side by side. Widths: 42 columns on 80 mm paper,
/// 32 on 58 mm. Long text wraps instead of being cut off.
/// </summary>
public sealed class ReceiptBuilder
{
    private static readonly byte[] Init = [0x1B, 0x40];
    private static readonly byte[] CutPaper = [0x1D, 0x56, 0x42, 0x00];

    private readonly MemoryStream _bytes = new();
    private readonly StringBuilder _text = new();

    public ReceiptBuilder(int columns)
    {
        if (columns < 16) throw new ArgumentOutOfRangeException(nameof(columns));
        Columns = columns;
        Write(Init);
    }

    public int Columns { get; }

    public static int ColumnsFor(int paperWidthMm) => paperWidthMm == 58 ? 32 : 42;

    /// <summary>The widest logo, in printer dots, that suits the paper.</summary>
    public static int LogoWidthFor(int paperWidthMm) => paperWidthMm == 58 ? 256 : 384;

    public ReceiptBuilder Text(string text, TextStyle style = TextStyle.None)
    {
        SetStyle(style);
        foreach (var line in Wrap(text, Columns))
        {
            WriteText(line);
            Write(0x0A);
            _text.AppendLine(style.HasFlag(TextStyle.Center) ? CenterPad(line) : line);
        }
        SetStyle(TextStyle.None);
        return this;
    }

    /// <summary>Left text and right text on one line, e.g. an item and its amount.</summary>
    public ReceiptBuilder Columns2(string left, string right, TextStyle style = TextStyle.None)
    {
        style &= ~TextStyle.Center;
        var room = Columns - right.Length - 1;
        if (room < 8)
            return Text(left, style).Text(right.PadLeft(Columns), style);

        var leftLines = Wrap(left, room).ToList();
        SetStyle(style);
        for (var i = 0; i < leftLines.Count; i++)
        {
            var line = i == leftLines.Count - 1
                ? leftLines[i].PadRight(room) + " " + right
                : leftLines[i];
            WriteText(line);
            Write(0x0A);
            _text.AppendLine(line);
        }
        SetStyle(TextStyle.None);
        return this;
    }

    public ReceiptBuilder Separator(char c = '-')
    {
        var line = new string(c, Columns);
        WriteText(line);
        Write(0x0A);
        _text.AppendLine(line);
        return this;
    }

    public ReceiptBuilder Feed(int lines = 1)
    {
        lines = Math.Clamp(lines, 1, 10);
        Write(0x1B, 0x64, (byte)lines);
        for (var i = 0; i < lines; i++) _text.AppendLine();
        return this;
    }

    public ReceiptBuilder Cut()
    {
        Write(CutPaper);
        _text.AppendLine(CenterPad("[cut]"));
        return this;
    }

    /// <summary>Prints the logo as a raster image, centred. Does nothing when there is no logo.</summary>
    public ReceiptBuilder Logo(MonoBitmap? logo)
    {
        if (logo is null) return this;
        Write(0x1B, 0x61, 0x01);
        var xBytes = logo.BytesPerRow;
        Write(0x1D, 0x76, 0x30, 0x00,
            (byte)(xBytes & 0xFF), (byte)(xBytes >> 8),
            (byte)(logo.Height & 0xFF), (byte)(logo.Height >> 8));
        Write(logo.Data);
        Write(0x0A);
        Write(0x1B, 0x61, 0x00);
        _text.AppendLine(CenterPad("[logo]"));
        return this;
    }

    public PrintDocument Build() => new(_bytes.ToArray(), _text.ToString());

    private void SetStyle(TextStyle style)
    {
        Write(0x1B, 0x61, (byte)(style.HasFlag(TextStyle.Center) ? 1 : 0));
        Write(0x1B, 0x45, (byte)(style.HasFlag(TextStyle.Bold) ? 1 : 0));
        Write(0x1D, 0x21, (byte)(style.HasFlag(TextStyle.DoubleHeight) ? 0x01 : 0x00));
    }

    private string CenterPad(string line) =>
        new string(' ', Math.Max(0, (Columns - line.Length) / 2)) + line;

    private void WriteText(string line) => Write(ToPrinterBytes(line));

    private void Write(params byte[] data) => _bytes.Write(data);

    /// <summary>Printers use a single-byte code page; accents are dropped and other symbols become '?'.</summary>
    internal static byte[] ToPrinterBytes(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(ch is >= ' ' and <= '~' ? ch : '?');
        }
        return Encoding.ASCII.GetBytes(plain.ToString());
    }

    internal static IEnumerable<string> Wrap(string text, int width)
    {
        if (text.Length == 0)
        {
            yield return "";
            yield break;
        }
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            var rest = word;
            while (rest.Length > width)
            {
                if (line.Length > 0)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                yield return rest[..width];
                rest = rest[width..];
            }
            if (line.Length > 0 && line.Length + 1 + rest.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(rest);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
