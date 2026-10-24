using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Printing;

namespace Tausi.Application.Reports;

/// <summary>A report in the list, with whether this user may run it.</summary>
public sealed record ReportListItem(ReportDefinition Definition);

/// <summary>A work period to choose from: its business date and whether it is still open.</summary>
public sealed record WorkPeriodOption(Guid Id, DateOnly Date, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, bool Open);

/// <summary>A choice for a filter: its id and what people see.</summary>
public sealed record ReportOption(string Id, string Name, string? Detail = null);

/// <summary>Everything the filter controls offer, in one call.</summary>
public sealed record ReportOptions(
    IReadOnlyList<WorkPeriodOption> Periods, IReadOnlyList<ReportOption> Users, IReadOnlyList<ReportOption> Terminals,
    IReadOnlyList<ReportOption> Locations, IReadOnlyList<ReportOption> Categories, IReadOnlyList<ReportOption> Suppliers,
    IReadOnlyList<ReportOption> Tables, IReadOnlyList<ReportOption> Roles, IReadOnlyList<ReportOption> Counts,
    IReadOnlyList<ReportOption> StockItems, IReadOnlyList<ReportOption> Printers);

/// <summary>A file to send back: Excel, CSV or PDF.</summary>
public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// The admin Reports page: the reports this user may run (each needs <c>reports.all</c>, the audit
/// log <c>permissions.manage</c>), running one, and its file and printer versions. Only Allowed
/// counts: the admin site has no approval pad.
/// </summary>
public sealed class ReportService(
    ITausiDb db, ICurrentSession session, IPermissionService permissions, TimeProvider clock, IEnumerable<IReport> reports, PrintQueue queue)
{
    /// <summary>The order of the wireframe's tab row, then the reports added by Task 13.</summary>
    public static readonly string[] Order =
    [
        "sales-by-user", "sales-by-item", "payments", "voids", "price-changes", "stock-on-hand", "stock-variance",
        "purchases", "daily-summary", "vat", "tickets", "users", "audit",
    ];

    private IEnumerable<IReport> Ordered => reports.OrderBy(r => Array.IndexOf(Order, r.Definition.Key) is var i and >= 0 ? i : int.MaxValue);

    public async Task<IReadOnlyList<ReportDefinition>> List(CancellationToken ct)
    {
        var user = session.RequireUser();
        var list = new List<ReportDefinition>();
        foreach (var report in Ordered)
            if (await permissions.Check(user, report.Definition.Permission, ct) == PermissionValue.Allowed)
                list.Add(report.Definition);
        return list;
    }

    /// <summary>The work periods to choose from, newest first (the last 120).</summary>
    public async Task<IReadOnlyList<WorkPeriodOption>> Periods(CancellationToken ct)
    {
        await RequireAny(ct);
        var zone = await Money.BusinessDates.Zone(db, ct);
        var periods = await db.WorkPeriods.AsNoTracking().OrderByDescending(p => p.OpenedAt).Take(120)
            .Select(p => new { p.Id, p.BusinessDate, p.OpenedAt, p.ClosedAt, p.IsOpen }).ToListAsync(ct);
        return periods.Select(p => new WorkPeriodOption(p.Id, p.BusinessDate ?? Money.BusinessDates.Of(p.OpenedAt, zone), p.OpenedAt, p.ClosedAt, p.IsOpen))
            .ToList();
    }

    public async Task<ReportOptions> Options(CancellationToken ct)
    {
        var periods = await Periods(ct);
        var zone = await Money.BusinessDates.Zone(db, ct);
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Name)
            .Select(u => new ReportOption(u.Id.ToString(), u.Name, u.Role != null ? u.Role.Name : null)).ToListAsync(ct);
        var terminals = await db.Terminals.AsNoTracking().OrderBy(t => t.Name).Select(t => new ReportOption(t.Id.ToString(), t.Name, null)).ToListAsync(ct);
        var locations = await db.StockLocations.AsNoTracking().OrderBy(l => l.Name).Select(l => new ReportOption(l.Id.ToString(), l.Name, null)).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new ReportOption(x.Id.ToString(), x.Name, null)).ToListAsync(ct);
        var suppliers = await db.Suppliers.AsNoTracking().OrderBy(x => x.Name).Select(x => new ReportOption(x.Id.ToString(), x.Name, null)).ToListAsync(ct);
        var tables = await db.Tables.AsNoTracking().OrderBy(x => x.Name).Select(x => new ReportOption(x.Id.ToString(), x.Name, null)).ToListAsync(ct);
        var roles = await db.Roles.AsNoTracking().OrderBy(x => x.Name).Select(x => new ReportOption(x.Id.ToString(), x.Name, null)).ToListAsync(ct);
        var locationNames = locations.ToDictionary(l => l.Id, l => l.Name);
        var counts = (await db.StockCounts.AsNoTracking().Where(k => k.Status == Domain.Stock.StockCountStatus.Approved && !k.IsOpening)
                .OrderByDescending(k => k.AsAt).Take(50).Select(k => new { k.Id, k.AsAt, k.LocationId }).ToListAsync(ct))
            .Select(k => new ReportOption(k.Id.ToString(),
                $"{TimeZoneInfo.ConvertTime(k.AsAt, zone):d MMM yyyy HH:mm} · {locationNames.GetValueOrDefault(k.LocationId.ToString(), "?")}", null))
            .ToList();
        var stockItems = await db.StockItems.AsNoTracking().OrderBy(i => i.Name).Select(i => new ReportOption(i.Id.ToString(), i.Name, null)).ToListAsync(ct);
        var printers = await db.Printers.AsNoTracking().Where(p => p.Active).OrderBy(p => p.Name)
            .Select(p => new ReportOption(p.Id.ToString(), p.Name, null)).ToListAsync(ct);
        return new ReportOptions(periods, users, terminals, locations, categories, suppliers, tables, roles, counts, stockItems, printers);
    }

    /// <summary>
    /// The supervisors' slip from the Close day screen: Sales by user for the open day on this
    /// terminal's receipt printer (needs reports.all).
    /// </summary>
    public async Task PrintSalesByUserHere(CancellationToken ct)
    {
        var user = session.RequireUser();
        if (user.TerminalId is not { } terminalId) throw new ForbiddenException("This slip prints on a terminal.", "terminal_required");
        var report = await Require("sales-by-user", ct);
        var terminal = await db.Terminals.AsNoTracking().Include(t => t.ReceiptPrinter).FirstAsync(t => t.Id == terminalId, ct);
        if (terminal.ReceiptPrinter is not { Active: true } printer)
            throw new DomainException("This terminal has no receipt printer. Set one in the admin site under Terminals and printers.");
        var context = await ReportContext.Create(db, new ReportFilters(), clock, ct);
        var result = await report.Run(context, ct);
        queue.Enqueue(printer, PrintJobKind.CashUp, ReportSlip.Build(result, context.Business, context.Zone, printer.Columns, printer.CutAfterSlip),
            label: result.Title);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ReportResult> Run(string key, ReportFilters filters, CancellationToken ct) => (await RunWith(key, filters, ct)).Result;

    private async Task<(ReportResult Result, ReportContext Context)> RunWith(string key, ReportFilters filters, CancellationToken ct)
    {
        var report = await Require(key, ct);
        var context = await ReportContext.Create(db, filters, clock, ct);
        return (await report.Run(context, ct), context);
    }

    public async Task<ReportFile> Export(string key, ReportFilters filters, string format, CancellationToken ct)
    {
        if (format.ToLowerInvariant() is not ("xlsx" or "csv")) throw new DomainException("Choose xlsx or csv.", "format");
        var (result, context) = await RunWith(key, filters, ct);
        var name = $"{key}-{context.Local(result.RunAt):yyyyMMdd-HHmm}";
        return format.ToLowerInvariant() switch
        {
            "xlsx" => new ReportFile(ReportExcel.Build(result, context.Zone), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                name + ".xlsx"),
            "csv" => new ReportFile(ReportCsv.Build(result, context.Zone), "text/csv; charset=utf-8", name + ".csv"),
            _ => throw new DomainException("Choose xlsx or csv.", "format"),
        };
    }

    public async Task<ReportFile> Pdf(string key, ReportFilters filters, CancellationToken ct)
    {
        var (result, context) = await RunWith(key, filters, ct);
        return new ReportFile(ReportPdf.Build(result, context.Business, context.Zone), "application/pdf", $"{key}-{result.RunAt:yyyyMMdd-HHmm}.pdf");
    }

    /// <summary>The short version on a receipt printer (reports marked for it only).</summary>
    public async Task PrintReceipt(string key, ReportFilters filters, Guid printerId, CancellationToken ct)
    {
        var report = await Require(key, ct);
        if (!report.Definition.Receipt) throw new DomainException("This report prints on A4 only.", "no_receipt");
        var printer = await db.Printers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == printerId && p.Active, ct)
            ?? throw new DomainException("Choose a printer that is switched on.");
        var context = await ReportContext.Create(db, filters, clock, ct);
        var result = await report.Run(context, ct);
        queue.Enqueue(printer, PrintJobKind.CashUp, ReportSlip.Build(result, context.Business, context.Zone, printer.Columns, printer.CutAfterSlip),
            label: result.Title);
        await db.SaveChangesAsync(ct);
    }

    private async Task<IReport> Require(string key, CancellationToken ct)
    {
        var report = reports.FirstOrDefault(r => r.Definition.Key == key) ?? throw new NotFoundException("Report not found.");
        var user = session.RequireUser();
        if (await permissions.Check(user, report.Definition.Permission, ct) != PermissionValue.Allowed)
            throw new PermissionDeniedException(report.Definition.Permission);
        return report;
    }

    private async Task RequireAny(CancellationToken ct)
    {
        var user = session.RequireUser();
        foreach (var key in new[] { PermissionKeys.ReportsAll, PermissionKeys.PermissionsManage })
            if (await permissions.Check(user, key, ct) == PermissionValue.Allowed) return;
        throw new PermissionDeniedException(PermissionKeys.ReportsAll);
    }
}
