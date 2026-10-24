using System.Globalization;
using System.Text;

namespace Tausi.Application.Printing;

/// <summary>
/// A small PDF writer for plain A4 sheets (the stock count sheet, reports): text in the built-in
/// Helvetica fonts, lines, boxes and grey fills, portrait or landscape. No fonts to embed and no package to licence. Coordinates are in points
/// from the top-left corner.
/// </summary>
public sealed class SimplePdf
{
    public const float PageWidth = 595.28f;
    public const float PageHeight = 841.89f;

    private readonly List<StringBuilder> _pages = [];
    private StringBuilder _page = null!;

    /// <summary>A4 portrait; <paramref name="landscape"/> turns it on its side (wide reports).</summary>
    public SimplePdf(bool landscape = false)
    {
        PaperWidth = landscape ? PageHeight : PageWidth;
        PaperHeight = landscape ? PageWidth : PageHeight;
        NewPage();
    }

    /// <summary>This document's page size in points.</summary>
    public float PaperWidth { get; }
    public float PaperHeight { get; }

    public int PageCount => _pages.Count;

    /// <summary>Draws on an earlier page again (0-based), e.g. to add "Page 1 of 3" once all pages exist.</summary>
    public SimplePdf UsePage(int index)
    {
        _page = _pages[index];
        return this;
    }

    public SimplePdf NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
        return this;
    }

    public SimplePdf Text(float x, float y, string text, float size = 10, bool bold = false)
    {
        _page.Append(CultureInfo.InvariantCulture,
            $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {PaperHeight - y:0.##} Td ({Escape(text)}) Tj ET\n");
        return this;
    }

    public SimplePdf Line(float x1, float y1, float x2, float y2, float width = 0.5f)
    {
        _page.Append(CultureInfo.InvariantCulture,
            $"{width:0.##} w {x1:0.##} {PaperHeight - y1:0.##} m {x2:0.##} {PaperHeight - y2:0.##} l S\n");
        return this;
    }

    public SimplePdf Box(float x, float y, float width, float height, float lineWidth = 0.5f)
    {
        _page.Append(CultureInfo.InvariantCulture,
            $"{lineWidth:0.##} w {x:0.##} {PaperHeight - y - height:0.##} {width:0.##} {height:0.##} re S\n");
        return this;
    }

    /// <summary>A box filled with a grey (0 black … 1 white), without a border.</summary>
    public SimplePdf Fill(float x, float y, float width, float height, float grey)
    {
        _page.Append(CultureInfo.InvariantCulture,
            $"q {grey:0.###} g {x:0.##} {PaperHeight - y - height:0.##} {width:0.##} {height:0.##} re f Q\n");
        return this;
    }

    /// <summary>Roughly how wide text is in Helvetica, to fit or right-align it.</summary>
    public static float Width(string text, float size) => text.Length * size * 0.5f;

    /// <summary>How wide text is in Helvetica (or Helvetica-Bold), from the fonts' own character widths.</summary>
    public static float Measure(string text, float size, bool bold = false)
    {
        var widths = bold ? BoldWidths : RegularWidths;
        var total = 0;
        foreach (var c in text) total += c is >= ' ' and <= '~' ? widths[c - ' '] : 556;
        return total * size / 1000f;
    }

    // Helvetica and Helvetica-Bold widths (thousandths of the font size) for ' ' to '~', from the Adobe font metrics.
    private static readonly int[] RegularWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly int[] BoldWidths =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    public byte[] Build()
    {
        var latin1 = Encoding.Latin1;
        var objects = new List<byte[]>();
        int Add(string body)
        {
            objects.Add(latin1.GetBytes(body));
            return objects.Count;
        }

        // 1 catalog, 2 pages, 3 and 4 fonts; then a page and its content per page.
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add(""); // pages, filled in below
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        var kids = new List<int>();
        foreach (var page in _pages)
        {
            var content = latin1.GetBytes(page.ToString());
            var contentId = objects.Count + 2;
            kids.Add(Add(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PaperWidth:0.##} {PaperHeight:0.##}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>")));
            objects.Add([.. latin1.GetBytes($"<< /Length {content.Length} >>\nstream\n"), .. content, .. latin1.GetBytes("\nendstream")]);
        }
        objects[1] = latin1.GetBytes($"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");

        using var output = new MemoryStream();
        void Write(string s) => output.Write(latin1.GetBytes(s));
        Write("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n");
            output.Write(objects[i]);
            Write("\nendobj\n");
        }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>Escapes PDF string delimiters; characters outside Latin-1 become "?".</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var ch = c switch
            {
                '–' or '—' => '-',
                '→' => '>',
                '’' => '\'',
                _ => c > 0xFF ? '?' : c,
            };
            if (ch is '\\' or '(' or ')') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
