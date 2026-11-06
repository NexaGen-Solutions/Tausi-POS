using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Printing;

namespace Tausi.Application.Reports;

/// <summary>The terminal's filters: dates (a quick pick, or from and to), and for <c>reports.all</c> a user and a terminal.</summary>
public sealed record TerminalReportFilters
{
    public QuickRange? Quick { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public Guid? UserId { get; init; }
    public Guid? TerminalId { get; init; }
}

/// <summary>A terminal report: the result, and whether it holds only the user's own figures.</summary>
public sealed record TerminalReport(bool OwnOnly, ReportResult Result);

/// <summary>
/// The terminal's Reports screen (Task 17b): Sales by user, Daily sales and Sales by item, run by the
/// same report classes as the admin site. Revenue only: cost, profit and margin are taken out for
/// everyone. With <c>reports.all</c> a user sees everyone; with only <c>reports.own</c> the server sets
/// the user filter to them, whatever the request asks for. Only Allowed counts.
/// </summary>
public sealed class TerminalReports(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, TimeProvider clock, IEnumerable<IReport> reports, PrintQueue queue)
{
    public static readonly string[] Keys = ["sales-by-user", "daily-sales", "sales-by-item"];

    /// <summary>Columns that are about cost, never shown on the terminal.</summary>
    public static readonly string[] CostColumns = ["cost", "profit", "margin"];

    public async Task<TerminalReport> Run(string key, TerminalReportFilters request, CancellationToken ct)
    {
        var (report, filters, own) = await Prepare(key, request, ct);
        var context = await ReportContext.Create(db, filters, clock, ct);
        return new TerminalReport(own, RevenueOnly(await report.Run(context, ct)));
    }

    /// <summary>The report on screen, on this terminal's receipt printer, with who printed it.</summary>
    public async Task Print(string key, TerminalReportFilters request, CancellationToken ct)
    {
        var (report, filters, _) = await Prepare(key, request, ct);
        var me = session.RequireUser();
        if (me.TerminalId is not { } terminalId) throw new ForbiddenException("This slip prints on a terminal.", "terminal_required");
        var terminal = await db.Terminals.AsNoTracking().Include(t => t.ReceiptPrinter).FirstAsync(t => t.Id == terminalId, ct);
        if (terminal.ReceiptPrinter is not { Active: true } printer)
            throw new DomainException("This terminal has no receipt printer. Set one in the admin site under Terminals and printers.");
        var context = await ReportContext.Create(db, filters, clock, ct);
        var result = RevenueOnly(await report.Run(context, ct));
        if (key == "sales-by-item") result = OneLinePerItem(result);
        queue.Enqueue(printer, PrintJobKind.CashUp,
            ReportSlip.Build(result, context.Business, context.Zone, printer.Columns, printer.CutAfterSlip, printedBy: me.Name), label: result.Title);
        await db.SaveChangesAsync(ct);
    }

    private async Task<(IReport Report, ReportFilters Filters, bool Own)> Prepare(string key, TerminalReportFilters r, CancellationToken ct)
    {
        if (!Keys.Contains(key)) throw new NotFoundException("Report not found.");
        var report = reports.First(x => x.Definition.Key == key);
        var me = session.RequireUser();
        var all = await permissions.Check(me, PermissionKeys.ReportsAll, ct) == PermissionValue.Allowed;
        if (!all && await permissions.Check(me, PermissionKeys.ReportsOwn, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(PermissionKeys.ReportsOwn);
        var dates = r.From is null && r.Quick is null ? QuickRange.Today : r.Quick;
        var filters = new ReportFilters
        {
            Quick = dates,
            From = dates is null ? r.From : null,
            To = dates is null ? r.To : null,
            // Only your own figures: the server decides, the request can't widen it.
            UserId = all ? r.UserId : me.UserId,
            TerminalId = all ? r.TerminalId : null,
            CreditTo = CreditTo.AddedBy,
        };
        return (report, filters, !all);
    }

    /// <summary>
    /// Sales by item on paper: one line per item ("6 x Tusker Lager … 1,200", the sales after
    /// discounts), and each category's gifts and voids in its footer. The same figures as the screen.
    /// </summary>
    public static ReportResult OneLinePerItem(ReportResult result)
    {
        ReportTable Table(ReportTable t)
        {
            int At(string key) => t.Columns.ToList().FindIndex(c => c.Key == key);
            var (item, qty, gross, discount, gifts, voids) = (At("item"), At("qty"), At("gross"), At("discount"), At("gifts"), At("voids"));
            if (item < 0 || qty < 0 || gross < 0 || discount < 0) return t;
            decimal D(ReportRow r, int i) => i >= 0 && r.Cells[i] is decimal d ? d : 0;
            ReportRow? Row(ReportRow? r) => r is null ? null : r with { Cells = [r.Cells[item], r.Cells[qty], D(r, gross) - D(r, discount)] };
            return t with
            {
                Columns = [t.Columns[item], t.Columns[qty], new ReportColumn("amount", "Sales", ColumnType.Money)],
                Groups = t.Groups.Select(g => g with
                {
                    Rows = g.Rows.Select(r => Row(r)!).ToList(),
                    Subtotal = Row(g.Subtotal),
                    Footer = g.Subtotal is { } sub
                        ? [$"Gifts {ReportFormat.Text(D(sub, gifts), ColumnType.Quantity, TimeZoneInfo.Utc)} · Voids {ReportFormat.Text(D(sub, voids), ColumnType.Quantity, TimeZoneInfo.Utc)}"]
                        : g.Footer,
                }).ToList(),
                Totals = Row(t.Totals),
            };
        }

        return result with { Tables = result.Tables.Select(Table).ToList() };
    }

    /// <summary>The result without its cost, profit and margin columns, or notes about them.</summary>
    public static ReportResult RevenueOnly(ReportResult result)
    {
        ReportTable Table(ReportTable t)
        {
            var keep = t.Columns.Select((c, i) => (c, i)).Where(x => !CostColumns.Contains(x.c.Key)).Select(x => x.i).ToList();
            ReportRow? Row(ReportRow? row) => row is null ? null : row with { Cells = keep.Select(i => i < row.Cells.Count ? row.Cells[i] : null).ToList() };
            return t with
            {
                Columns = keep.Select(i => t.Columns[i]).ToList(),
                Groups = t.Groups.Select(g => g with { Rows = g.Rows.Select(x => Row(x)!).ToList(), Subtotal = Row(g.Subtotal) }).ToList(),
                Totals = Row(t.Totals),
            };
        }

        static bool AboutCost(string note) =>
            note.Contains("cost", StringComparison.OrdinalIgnoreCase) || note.Contains("profit", StringComparison.OrdinalIgnoreCase) ||
            note.Contains("recipe", StringComparison.OrdinalIgnoreCase) || note.Contains("margin", StringComparison.OrdinalIgnoreCase);

        return result with { Tables = result.Tables.Select(Table).ToList(), Notes = result.Notes.Where(n => !AboutCost(n)).ToList() };
    }
}
