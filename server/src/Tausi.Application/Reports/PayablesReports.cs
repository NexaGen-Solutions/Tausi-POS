using Microsoft.EntityFrameworkCore;
using Tausi.Application.Money;
using Tausi.Domain.People;

namespace Tausi.Application.Reports;

/// <summary>
/// The supplier ledger: per supplier, what they were owed before the period ("Brought forward"),
/// then every delivery on credit, payment, opening balance and reversal in it with the running
/// balance. It is read from the Supplier payables lines of the journal (like Purchases), so the
/// balance always equals the ledger.
/// </summary>
public sealed class SupplierStatementReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("supplier-statement", "Supplier statement", PermissionKeys.ReportsAll,
        [ReportFilter.Supplier]);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var (from, to) = (c.Period.From, c.Period.To);
        var moves = await PurchasesReport.PayablesMoves(c, to, ct);
        var suppliers = await c.Db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var ids = moves.GroupBy(m => m.Supplier)
            .Where(g => g.Any(m => m.Date >= from) || g.Where(m => m.Date < from).Sum(m => m.Amount) != 0)
            .Select(g => g.Key)
            .Concat(c.Filters.SupplierId is { } chosen ? [chosen] : [])
            .Where(s => c.Filters.SupplierId is null || s == c.Filters.SupplierId)
            .Distinct().OrderBy(s => suppliers.GetValueOrDefault(s, "?")).ToList();

        decimal more = 0, less = 0, owed = 0;
        var groups = ids.Select(s =>
        {
            var balance = moves.Where(m => m.Supplier == s && m.Date < from).Sum(m => m.Amount);
            var rows = new List<ReportRow> { ReportRow.Of(from, "Brought forward", null, null, null, balance) };
            foreach (var m in moves.Where(m => m.Supplier == s && m.Date >= from).OrderBy(m => m.Date).ThenBy(m => m.At))
            {
                balance += m.Amount;
                rows.Add(ReportRow.Of(m.Date, Entry(m), m.Detail.Length == 0 ? null : m.Detail,
                    m.Amount > 0 ? m.Amount : null, m.Amount < 0 ? -m.Amount : null, balance));
            }
            var mine = moves.Where(m => m.Supplier == s && m.Date >= from).ToList();
            more += mine.Where(m => m.Amount > 0).Sum(m => m.Amount);
            less += mine.Where(m => m.Amount < 0).Sum(m => -m.Amount);
            owed += balance;
            return new ReportGroup(suppliers.GetValueOrDefault(s, "?"), rows, null,
                [$"Owed at the end: KSh {ReportFormat.Money(balance)}"], Total: balance);
        }).ToList();

        var table = new ReportTable(null,
        [
            new("date", "Date", ColumnType.Date), new("entry", "Entry"), new("detail", "Invoice or payment"),
            new("more", "Owed more", ColumnType.Money), new("less", "Owed less", ColumnType.Money), new("balance", "Balance", ColumnType.Money),
        ],
        groups, ReportRow.Of("Total", null, null, more, less, owed),
        [$"Owed to suppliers at the end: KSh {ReportFormat.Money(owed)}"]);
        return await Result(c, [table], ct);
    }

    private static string Entry(PurchasesReport.PayablesMove m) => (m.Kind, m.Reversal) switch
    {
        ("delivery", false) => "Delivery on credit",
        ("delivery", true) => "Delivery reversed",
        ("payment", false) => "Payment",
        ("payment", true) => "Payment reversed",
        (_, false) => "Opening balance",
        (_, true) => "Opening balance reversed",
    };
}

/// <summary>
/// What each supplier is owed on a date, by how old it is: current (up to 30 days), 31–60, 61–90 and
/// over 90 days from the delivery (or opening balance) date. Payments and reversals settle the oldest
/// first. A supplier paid ahead shows a negative amount under Current.
/// </summary>
public sealed class PayablesAgingReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("payables-aging", "Payables aging", PermissionKeys.ReportsAll,
        [ReportFilter.AsAt, ReportFilter.Supplier], UsesPeriod: false);

    public static readonly int[] Buckets = [30, 60, 90];

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var asAt = c.Filters.AsAt ?? BusinessDates.Of(c.Now, c.Zone);
        var moves = (await PurchasesReport.PayablesMoves(c, asAt, ct))
            .Where(m => c.Filters.SupplierId is null || m.Supplier == c.Filters.SupplierId).ToList();
        var suppliers = await c.Db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var totals = new decimal[Buckets.Length + 2];
        var rows = new List<ReportRow>();
        foreach (var group in moves.GroupBy(m => m.Supplier).OrderBy(g => suppliers.GetValueOrDefault(g.Key, "?")))
        {
            var aged = Age(group, asAt);
            if (aged.All(a => a == 0)) continue;
            for (var i = 0; i < aged.Length; i++) totals[i] += aged[i];
            rows.Add(ReportRow.Of([suppliers.GetValueOrDefault(group.Key, "?"), .. aged.Cast<object?>()]));
        }

        var table = new ReportTable(null,
        [
            new("supplier", "Supplier"), new("current", "Current (0–30 days)", ColumnType.Money), new("d60", "31–60 days", ColumnType.Money),
            new("d90", "61–90 days", ColumnType.Money), new("over", "Over 90 days", ColumnType.Money), new("total", "Total owed", ColumnType.Money),
        ],
        [new ReportGroup(null, rows)], ReportRow.Of(["Total", .. totals.Cast<object?>()]),
        ["Days from the delivery date. Payments settle the oldest deliveries first."]);
        return await Result(c, [table], ct, $"As at {asAt:d MMM yyyy}");
    }

    /// <summary>One supplier's balance in the buckets, then the total.</summary>
    public static decimal[] Age(IEnumerable<PurchasesReport.PayablesMove> moves, DateOnly asAt)
    {
        var list = moves.OrderBy(m => m.Date).ThenBy(m => m.At).ToList();
        var open = list.Where(m => m.Amount > 0).Select(m => (m.Date, Left: m.Amount)).ToList();
        var settled = list.Where(m => m.Amount < 0).Sum(m => -m.Amount);
        for (var i = 0; i < open.Count && settled > 0; i++)
        {
            var take = Math.Min(open[i].Left, settled);
            open[i] = (open[i].Date, open[i].Left - take);
            settled -= take;
        }
        var result = new decimal[Buckets.Length + 2];
        foreach (var (date, left) in open.Where(o => o.Left > 0))
        {
            var days = asAt.DayNumber - date.DayNumber;
            var bucket = Array.FindIndex(Buckets, b => days <= b);
            result[bucket < 0 ? Buckets.Length : bucket] += left;
        }
        result[0] -= settled; // paid ahead
        result[^1] = result[..^1].Sum();
        return result;
    }
}
