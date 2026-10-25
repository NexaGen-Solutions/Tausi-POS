using ClosedXML.Excel;
using Tausi.Domain.Common;

namespace Tausi.Application.Menu;

/// <summary>A menu file's rows as text: CSV, or the first sheet of an Excel (.xlsx) file.</summary>
public static class MenuFile
{
    public static List<List<string>> Rows(byte[] file)
    {
        if (!IsXlsx(file)) return MenuCsv.Parse(MenuCsv.Decode(file));
        try
        {
            using var book = new XLWorkbook(new MemoryStream(file));
            var sheet = book.Worksheets.First();
            var used = sheet.RangeUsed();
            if (used is null) return [];
            var width = used.LastColumn().ColumnNumber();
            return used.Rows()
                .Select(r => Enumerable.Range(1, width).Select(c => r.Cell(c).GetFormattedString().Trim()).ToList())
                .Where(r => r.Any(c => c.Length > 0))
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            throw new DomainException("That Excel file can't be read. Save it again as .xlsx or CSV.", MenuCodes.ImportErrors);
        }
    }

    /// <summary>An .xlsx file is a zip: it starts with "PK".</summary>
    public static bool IsXlsx(byte[] file) => file.Length > 4 && file[0] == 'P' && file[1] == 'K' && file[2] == 3 && file[3] == 4;
}
