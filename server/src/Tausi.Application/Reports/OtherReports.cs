using Microsoft.EntityFrameworkCore;
using Tausi.Application.Money;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;

namespace Tausi.Application.Reports;

/// <summary>
/// The cash book's daily summary (Task 12, read from the ledger) for a range of business dates:
/// a row per date and the range's totals. One date reads top to bottom, as in the cash book.
/// </summary>
public sealed class DailySummaryReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("daily-summary", "Daily summary", PermissionKeys.ReportsAll, [], Receipt: true);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var (from, to) = (c.Period.From, c.Period.To);
        if (from == to)
        {
            var day = await Summaries.For(c.Db, from, includeLate: true, ct);
            var table = new ReportTable(null, [new("line", "Line"), new("amount", "Amount", ColumnType.Money)],
                [new ReportGroup(null, CashBookPrint.Figures(day).Select(l => ReportRow.Of(l.Label, l.Amount)).ToList())]);
            return await Result(c, [table], ct, NoRecipe(day.NoRecipeItems.Count),
                "Cost of sales comes from recipes and stock cost, so profit is real, not an estimate.");
        }

        var rows = new List<DailySummaryDto>();
        for (var date = from; date <= to; date = date.AddDays(1)) rows.Add(await Summaries.For(c.Db, date, includeLate: true, ct));
        ReportRow Row(object label, IReadOnlyCollection<DailySummaryDto> d) => ReportRow.Of(label, d.Sum(x => x.SalesInclVat), d.Sum(x => x.Vat),
            d.Sum(x => x.NetSales), d.Sum(x => x.CostOfSales), d.Sum(x => x.GrossProfit), d.Sum(x => x.Expenses), d.Sum(x => x.StockLossAndCash),
            d.Sum(x => x.BankCharges), d.Sum(x => x.OtherIncome), d.Sum(x => x.Net));
        var range = new ReportTable(null,
        [
            new("date", "Date", ColumnType.Date), new("sales", "Sales (incl. VAT)", ColumnType.Money), new("vat", "VAT", ColumnType.Money),
            new("net", "Net sales", ColumnType.Money), new("cost", "Cost of sales", ColumnType.Money), new("gross", "Gross profit", ColumnType.Money),
            new("expenses", "Expenses", ColumnType.Money), new("loss", "Stock loss and cash differences", ColumnType.Money),
            new("bank", "Bank charges", ColumnType.Money), new("income", "Other income", ColumnType.Money), new("result", "Net", ColumnType.Money),
        ],
        [new ReportGroup(null, rows.Select(d => Row(d.Date, [d])).ToList())],
        Row("Total", rows));
        return await Result(c, [range], ct, NoRecipe(rows.SelectMany(r => r.NoRecipeItems).Distinct().Count()));
    }

    private static string? NoRecipe(int count) =>
        count == 0 ? null : $"Cost of sales is missing for {count} item{(count == 1 ? "" : "s")} with no recipe";
}

/// <summary>
/// The VAT summary per business day and rate (exempt and zero-rated on their own lines): gross
/// after discounts, net and VAT, gifts at 0. Settled tickets only, as the ledger posts VAT when a
/// ticket is settled, so it equals the VAT payable in the books for the same dates.
/// </summary>
public sealed class VatReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("vat", "VAT summary", PermissionKeys.ReportsAll, []);

    public static string RateLabel(decimal rate, bool exempt) => exempt ? "Exempt" : rate == 0 ? "Zero-rated" : $"VAT {rate:0.##}%";

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var dates = await c.Db.WorkPeriods.AsNoTracking().Where(p => c.Period.PeriodIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.BusinessDate ?? BusinessDates.Of(p.OpenedAt, c.Zone), ct);
        var byRate = await SalesQuery.Lines(c.Db, c.Period.PeriodIds, includeOpen: false)
            .GroupBy(l => new { l.WorkPeriodId, l.VatRate, l.VatExempt })
            .Select(g => new { g.Key.WorkPeriodId, g.Key.VatRate, g.Key.VatExempt, Gross = g.Sum(l => l.Gross - l.Discount), Net = g.Sum(l => l.Net), Vat = g.Sum(l => l.Vat) })
            .ToListAsync(ct);
        var rows = byRate.Select(r => new { Date = dates[r.WorkPeriodId], Label = RateLabel(r.VatRate, r.VatExempt), r.VatRate, r.VatExempt, r.Gross, r.Net, r.Vat })
            .GroupBy(r => (r.Date, r.Label, r.VatRate, r.VatExempt))
            .Select(g => new { g.Key.Date, g.Key.Label, g.Key.VatRate, g.Key.VatExempt, Gross = g.Sum(x => x.Gross), Net = g.Sum(x => x.Net), Vat = g.Sum(x => x.Vat) })
            .ToList();
        IReadOnlyList<ReportColumn> columns =
            [new("rate", "Rate"), new("gross", "Gross after discounts", ColumnType.Money), new("net", "Net", ColumnType.Money), new("vat", "VAT", ColumnType.Money)];
        var perDay = new ReportTable("Per day", columns,
            rows.GroupBy(r => r.Date).OrderBy(g => g.Key).Select(g => new ReportGroup(
                g.Key.ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
                g.OrderByDescending(r => r.VatRate).ThenBy(r => r.VatExempt).Select(r => ReportRow.Of(r.Label, r.Gross, r.Net, r.Vat)).ToList(),
                ReportRow.Of("Day total", g.Sum(r => r.Gross), g.Sum(r => r.Net), g.Sum(r => r.Vat)), Total: g.Sum(r => r.Vat))).ToList());
        var totals = new ReportTable("Totals by rate", columns,
            [new ReportGroup(null, rows.GroupBy(r => (r.Label, r.VatRate, r.VatExempt)).OrderByDescending(g => g.Key.VatRate).ThenBy(g => g.Key.VatExempt)
                .Select(g => ReportRow.Of(g.Key.Label, g.Sum(r => r.Gross), g.Sum(r => r.Net), g.Sum(r => r.Vat))).ToList())],
            ReportRow.Of("Total", rows.Sum(r => r.Gross), rows.Sum(r => r.Net), rows.Sum(r => r.Vat)));
        return await Result(c, [perDay, totals], ct, "Settled tickets only: VAT is due on what was paid.");
    }
}

/// <summary>
/// Every ticket in the period: numbers, table, who opened it, when it opened and settled, total,
/// discount, payment types, status and how often it was printed and reprinted; the starting point
/// for checking anything odd. Ticket numbers open the ticket detail.
/// </summary>
public sealed class TicketsReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("tickets", "Tickets and receipts", PermissionKeys.ReportsAll,
        [ReportFilter.Status, ReportFilter.User, ReportFilter.Table, ReportFilter.Amount, ReportFilter.Flags, ReportFilter.IncludeOpen]);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var ids = c.Period.PeriodIds;
        var query = c.Db.Tickets.AsNoTracking().Where(k => ids.Contains(k.WorkPeriodId));
        if (!c.IncludeOpen) query = query.Where(k => k.Status != TicketStatus.Open);
        if (f.Status is { } status) query = query.Where(k => k.Status == status);
        if (f.UserId is { } user) query = query.Where(k => k.OpenedByUserId == user);
        if (f.TableId is { } table) query = query.Where(k => k.TableId == table);
        if (f.HasDiscount) query = query.Where(k => k.DiscountKind != null);
        if (f.HasVoids) query = query.Where(k => c.Db.VoidLogs.Any(v => v.TicketId == k.Id));
        if (f.HasReprints) query = query.Where(k => c.Db.PrintJobs.Any(j => j.TicketId == k.Id && j.IsReprint));
        var tickets = await query.Select(k => new
            {
                k.Id, k.Number, k.ReceiptNumber, Table = k.Table != null ? k.Table.Name : k.CustomerName, k.OpenedByUserId, k.OpenedAt, k.SettledAt,
                k.Status,
                Total = k.Lines.Where(l => l.Status != OrderLineStatus.Cancelled && l.Status != OrderLineStatus.Voided && l.VoidedAt == null)
                    .Sum(l => (decimal?)(l.Gross - l.Discount)) ?? 0,
                Discount = k.Lines.Where(l => l.Status != OrderLineStatus.Cancelled && l.Status != OrderLineStatus.Voided && l.VoidedAt == null)
                    .Sum(l => (decimal?)l.Discount) ?? 0,
                Prints = c.Db.PrintJobs.Count(j => j.TicketId == k.Id && (j.Kind == PrintJobKind.Bill || j.Kind == PrintJobKind.Receipt) && !j.IsReprint),
                Reprints = c.Db.PrintJobs.Count(j => j.TicketId == k.Id && j.IsReprint),
            })
            .OrderBy(k => k.Number).ToListAsync(ct);
        if (f.MinAmount is { } min) tickets = tickets.Where(k => k.Total >= min).ToList();
        if (f.MaxAmount is { } max) tickets = tickets.Where(k => k.Total <= max).ToList();

        var types = await (from p in c.Db.Payments.AsNoTracking()
                           join k in c.Db.Tickets.AsNoTracking() on p.TicketId equals k.Id
                           where ids.Contains(k.WorkPeriodId) && p.Status != PaymentStatus.Removed
                           select new { p.TicketId, p.Type }).Distinct().ToListAsync(ct);
        string Paid(Guid ticket) => string.Join(", ", types.Where(t => t.TicketId == ticket).Select(t => t.Type).Distinct().OrderBy(t => t)
            .Select(t => t == PaymentType.Mpesa ? "M-Pesa" : t.ToString()));

        var rows = tickets.Select(k => new ReportRow([k.Number, k.ReceiptNumber, k.Table, c.UserName(k.OpenedByUserId), k.OpenedAt, k.SettledAt,
            k.Total, k.Discount, Paid(k.Id), k.Status.ToString(), k.Prints, k.Reprints], k.Id,
            Muted: k.Status is TicketStatus.Cancelled or TicketStatus.Merged)).ToList();
        var counted = tickets.Where(k => k.Status is TicketStatus.Settled or TicketStatus.Open).ToList();
        var result = new ReportTable(null,
        [
            new("ticket", "Ticket", ColumnType.Ticket), new("receipt", "Receipt", ColumnType.Integer), new("table", "Table"), new("opened", "Opened by"),
            new("openedAt", "Opened", ColumnType.DateTime), new("settledAt", "Settled", ColumnType.DateTime), new("total", "Total", ColumnType.Money),
            new("discount", "Discount", ColumnType.Money), new("payments", "Paid by"), new("status", "Status"),
            new("prints", "Prints", ColumnType.Integer), new("reprints", "Reprints", ColumnType.Integer),
        ],
        [new ReportGroup(null, rows)],
        ReportRow.Of($"{counted.Count} tickets", null, null, null, null, null, counted.Sum(k => k.Total), counted.Sum(k => k.Discount), null, null,
            tickets.Sum(k => k.Prints), tickets.Sum(k => k.Reprints)),
        tickets.GroupBy(k => k.Status).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}").ToList() is { Count: > 0 } byStatus
            ? [string.Join(" · ", byStatus)]
            : null);
        return await Result(c, [result], ct, await OpenNote(c, ct), await SalesByUserReport.RoundingNote(c, ct));
    }
}

/// <summary>Each user: their role, the terminals they logged in on, first and last login, tickets opened, items added, voids asked for and approvals given.</summary>
public sealed class UsersReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("users", "Users, roles and terminals", PermissionKeys.ReportsAll, [ReportFilter.Role]);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var ids = c.Period.PeriodIds;
        var (start, end) = (c.Period.Start, c.Period.End);
        var users = await c.Db.Users.AsNoTracking().Where(u => c.Filters.RoleId == null || u.RoleId == c.Filters.RoleId)
            .Select(u => new { u.Id, u.Name, u.Active, Role = u.Role != null ? u.Role.Name : "" }).ToListAsync(ct);
        var sessions = await c.Db.UserSessions.AsNoTracking().Where(s => s.StartedAt >= start && s.StartedAt <= end)
            .Select(s => new { s.UserId, s.TerminalId, s.StartedAt }).ToListAsync(ct);
        var tickets = await c.Db.Tickets.AsNoTracking().Where(k => ids.Contains(k.WorkPeriodId))
            .GroupBy(k => k.OpenedByUserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var items = await (from l in c.Db.OrderLines.AsNoTracking()
                           join k in c.Db.Tickets.AsNoTracking() on l.TicketId equals k.Id
                           where ids.Contains(k.WorkPeriodId) && l.Status != OrderLineStatus.Cancelled
                           group l by l.AddedByUserId into g
                           select new { g.Key, Quantity = g.Sum(l => l.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Quantity, ct);
        var inPeriod = c.Db.Tickets.AsNoTracking().Where(k => ids.Contains(k.WorkPeriodId)).Select(k => k.Id);
        var voids = await c.Db.VoidLogs.AsNoTracking().Where(v => inPeriod.Contains(v.TicketId))
            .Select(v => new { v.RequestedByUserId, v.ApprovedByUserId }).ToListAsync(ct);
        var gifts = await c.Db.GiftLogs.AsNoTracking().Where(g => inPeriod.Contains(g.TicketId))
            .Select(g => new { g.RequestedByUserId, g.ApprovedByUserId }).ToListAsync(ct);
        var discounts = await c.Db.DiscountLogs.AsNoTracking().Where(d => inPeriod.Contains(d.TicketId))
            .Select(d => new { d.RequestedByUserId, d.ApprovedByUserId }).ToListAsync(ct);
        var prices = await c.Db.PriceChangeLogs.AsNoTracking().Where(p => p.TicketId != null && inPeriod.Contains(p.TicketId.Value))
            .Select(p => new { RequestedByUserId = p.ChangedByUserId, p.ApprovedByUserId }).ToListAsync(ct);
        // An approval is someone else's say-so; doing it yourself (Allowed) isn't one. A request approved from the
        // approvals screen has its decider as the approver on the log, so it counts once.
        var approvals = voids.Select(v => (v.RequestedByUserId, (Guid?)v.ApprovedByUserId))
            .Concat(gifts.Select(g => (g.RequestedByUserId, (Guid?)g.ApprovedByUserId)))
            .Concat(discounts.Select(d => (d.RequestedByUserId, (Guid?)d.ApprovedByUserId)))
            .Concat(prices.Select(p => (p.RequestedByUserId, p.ApprovedByUserId)))
            .Where(a => a.Item2 is not null && a.Item2 != a.RequestedByUserId)
            .GroupBy(a => a.Item2!.Value).ToDictionary(g => g.Key, g => g.Count());

        var rows = users.Select(u =>
            {
                var mine = sessions.Where(s => s.UserId == u.Id).ToList();
                return new
                {
                    u.Name, u.Role, u.Active, Terminals = string.Join(", ", mine.Select(s => c.TerminalName(s.TerminalId)).Distinct().Order()),
                    First = mine.Count > 0 ? mine.Min(s => s.StartedAt) : (DateTimeOffset?)null,
                    Last = mine.Count > 0 ? mine.Max(s => s.StartedAt) : (DateTimeOffset?)null,
                    Tickets = tickets.GetValueOrDefault(u.Id), Items = items.GetValueOrDefault(u.Id),
                    Voids = voids.Count(v => v.RequestedByUserId == u.Id), Approvals = approvals.GetValueOrDefault(u.Id),
                };
            })
            .Where(r => r.Active || r.First is not null || r.Tickets > 0)
            .OrderBy(r => r.Role).ThenBy(r => r.Name)
            .ToList();
        var table = new ReportTable(null,
        [
            new("name", "User"), new("role", "Role"), new("terminals", "Terminals"), new("first", "First login", ColumnType.DateTime),
            new("last", "Last login", ColumnType.DateTime), new("tickets", "Tickets opened", ColumnType.Integer),
            new("items", "Items added", ColumnType.Quantity), new("voids", "Voids asked for", ColumnType.Integer),
            new("approvals", "Approvals given", ColumnType.Integer),
        ],
        rows.GroupBy(r => r.Role).Select(g => new ReportGroup(g.Key.Length == 0 ? "No role" : g.Key,
            g.Select(r => new ReportRow([r.Name, r.Role, r.Terminals, r.First, r.Last, r.Tickets, r.Items, r.Voids, r.Approvals], Muted: !r.Active))
                .ToList())).ToList());
        return await Result(c, [table], ct);
    }
}

/// <summary>The raw audit trail: time, user, terminal, action, entity and the values before and after. Needs permissions.manage.</summary>
public sealed class AuditReport : ReportBase
{
    public const int MaxRows = 5000;

    public override ReportDefinition Definition { get; } = new("audit", "Audit log", PermissionKeys.PermissionsManage,
        [ReportFilter.User, ReportFilter.Action, ReportFilter.Entity, ReportFilter.Search]);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var query = c.Db.AuditEntries.AsNoTracking().Where(a => a.At >= c.Period.Start && a.At <= c.Period.End);
        if (f.UserId is { } user) query = query.Where(a => a.UserId == user);
        if (!string.IsNullOrWhiteSpace(f.Action)) query = query.Where(a => a.Action.StartsWith(f.Action.Trim()));
        if (!string.IsNullOrWhiteSpace(f.Entity)) query = query.Where(a => a.EntityType == f.Entity.Trim());
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            query = query.Where(a => a.Action.Contains(s) || (a.EntityId != null && a.EntityId.Contains(s))
                                                          || (a.OldValueJson != null && a.OldValueJson.Contains(s))
                                                          || (a.NewValueJson != null && a.NewValueJson.Contains(s)));
        }
        var entries = await query.OrderBy(a => a.At).Take(MaxRows + 1)
            .Select(a => new { a.At, a.UserId, a.TerminalId, a.Action, a.EntityType, a.EntityId, a.OldValueJson, a.NewValueJson, a.ApprovedByUserId, a.Actor })
            .ToListAsync(ct);
        var cut = entries.Count > MaxRows;
        var rows = entries.Take(MaxRows).Select(a => ReportRow.Of(a.At, a.UserId is null ? a.Actor ?? "" : c.UserName(a.UserId), c.TerminalName(a.TerminalId), a.Action,
            a.EntityType is null ? null : $"{a.EntityType} {a.EntityId}".Trim(), c.UserName(a.ApprovedByUserId), a.OldValueJson, a.NewValueJson)).ToList();
        var table = new ReportTable(null,
        [
            new("at", "Time", ColumnType.DateTime), new("user", "User"), new("terminal", "Terminal"), new("action", "Action"), new("entity", "Entity"),
            new("approved", "Approved by"), new("before", "Before"), new("after", "After"),
        ],
        [new ReportGroup(null, rows)]);
        return await Result(c, [table], ct, cut ? $"Showing the first {MaxRows:#,##0} entries; narrow the filters to see the rest" : null);
    }
}
