using System.Globalization;
using System.Text;

namespace Tausi.Application.Printing;

/// <summary>
/// A small PDF writer for plain A4 sheets (the stock count sheet): text in the built-in Helvetica
/// fonts, lines and boxes. No fonts to embed and no package to licence. Coordinates are in points
/// from the top-left corner.
/// </summary>
public sealed class SimplePdf
{
    public const float PageWidth = 595.28f;
    public const float PageHeight = 841.89f;

    private readonly List<StringBuilder> _pages = [];
    private StringBuilder _page = null!;

    public SimplePdf() => NewPage();

    public int PageCount => _pages.Count;

    public SimplePdf NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
        return this;
    }

    public SimplePdf Text(float x, float y, string text, float size = 10, bool bold = false)
    {
        _page.Append(CultureInfo.InvariantCulture,
            $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {PageHeight - y:0.##} Td ({Escape(text)}) Tj ET\n");
        return this;
    }

    public SimplePdf Line(float x1, float y1, float x2, float y2, float width = 0.5f)
    {
        _page.Append(CultureInfo.InvariantCulture,
            $"{width:0.##} w {x1:0.##} {PageHeight - y1:0.##} m {x2:0.##} {PageHeight - y2:0.##} l S\n");
        return this;
    }

    public SimplePdf Box(float x, float y, float width, float height, float lineWidth = 0.5f)
    {
        _page.Append(CultureInfo.InvariantCulture,
            $"{lineWidth:0.##} w {x:0.##} {PageHeight - y - height:0.##} {width:0.##} {height:0.##} re S\n");
        return this;
    }

    /// <summary>Roughly how wide text is in Helvetica, to fit or right-align it.</summary>
    public static float Width(string text, float size) => text.Length * size * 0.5f;

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
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:0.##} {PageHeight:0.##}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>")));
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
