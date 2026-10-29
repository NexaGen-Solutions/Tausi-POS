using Microsoft.EntityFrameworkCore;
using Tausi.Application.Money;
using Tausi.Application.Stock;
using Tausi.Domain.Ledger;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Stock;

namespace Tausi.Application.Reports;

/// <summary>
/// Stock on hand as at the end of a date, worked out from the movements (not the cached levels):
/// each item per location in packs and base units, its average cost, value, reorder level and
/// whether it is below the reorder level or below zero; grouped by category with value subtotals.
/// The average cost is today's for today; for an earlier date it is replayed from the movements.
/// </summary>
public sealed class StockOnHandReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("stock-on-hand", "Stock on hand", PermissionKeys.ReportsAll,
        [ReportFilter.AsAt, ReportFilter.Location, ReportFilter.StockCategory, ReportFilter.OnlyBelowReorder], UsesPeriod: false);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var today = BusinessDates.Of(c.Now, c.Zone);
        var asAt = f.AsAt ?? today;
        var end = ReportContext.Midnight(asAt.AddDays(1), c.Zone);
        var quantities = await c.Db.StockMovements.AsNoTracking().Where(m => m.At < end)
            .GroupBy(m => new { m.StockItemId, m.LocationId })
            .Select(g => new { g.Key.StockItemId, g.Key.LocationId, OnHand = g.Sum(m => m.Quantity) })
            .ToListAsync(ct);
        var items = await c.Db.StockItems.AsNoTracking().ToDictionaryAsync(i => i.Id, ct);
        var locations = await c.Db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var reorder = await c.Db.StockLevels.AsNoTracking()
            .ToDictionaryAsync(l => (l.StockItemId, l.LocationId), l => l.ReorderLevel, ct);
        var averages = asAt >= today ? items.ToDictionary(i => i.Key, i => i.Value.AverageCost) : await AveragesAsAt(c, end, ct);

        var rows = quantities
            .Where(q => items.ContainsKey(q.StockItemId))
            .Where(q => f.LocationId is null || q.LocationId == f.LocationId)
            .Where(q => f.StockCategory is null || items[q.StockItemId].Category == f.StockCategory)
            .Select(q =>
            {
                var item = items[q.StockItemId];
                var level = reorder.GetValueOrDefault((q.StockItemId, q.LocationId));
                var average = averages.GetValueOrDefault(q.StockItemId);
                var below = level is { } r && q.OnHand < r;
                var status = q.OnHand < 0 ? "Below zero" : below ? "Below reorder" : null;
                return new
                {
                    Item = item, Location = locations.GetValueOrDefault(q.LocationId, "?"), q.OnHand, Average = average,
                    Value = MoneyMath.Round(q.OnHand * average), Reorder = level, Status = status, Low = q.OnHand < 0 || below,
                };
            })
            .Where(r => !f.OnlyBelowReorder || r.Low)
            .Where(r => r.OnHand != 0 || r.Reorder is not null || r.Item.Active)
            .ToList();

        var groups = rows.GroupBy(r => r.Item.Category).OrderBy(g => g.Key)
            .Select(g => new ReportGroup(Category(g.Key),
                g.OrderBy(r => r.Item.Name).ThenBy(r => r.Location)
                    .Select(r => new ReportRow([r.Item.Name, r.Location, r.Item.Describe(r.OnHand), r.OnHand, r.Item.BaseUnitName, r.Average, r.Value,
                        r.Reorder, r.Status], Muted: false)).ToList(),
                ReportRow.Of($"{Category(g.Key)} value", null, null, null, null, null, g.Sum(r => r.Value), null, null),
                Total: g.Sum(r => r.Value)))
            .ToList();
        var table = new ReportTable(null,
        [
            new("item", "Item"), new("location", "Location"), new("onHand", "On hand"), new("base", "Base units", ColumnType.Quantity),
            new("unit", "Unit"), new("average", "Average cost", ColumnType.Money), new("value", "Value", ColumnType.Money),
            new("reorder", "Reorder level", ColumnType.Quantity), new("status", "Status"),
        ],
        groups, ReportRow.Of("Total value", null, null, null, null, null, rows.Sum(r => r.Value), null, null));
        var low = rows.Count(r => r.Low);
        return await Result(c, [table], ct, low == 0 ? null : $"{low} below the reorder level or below zero");
    }

    public static string Category(StockCategory category) => category switch
    {
        StockCategory.SoftDrinks => "Soft drinks",
        _ => category.ToString(),
    };

    /// <summary>The business-wide average cost of each item at a time: openings set it, deliveries and their reversals move it.</summary>
    private static async Task<Dictionary<Guid, decimal>> AveragesAsAt(ReportContext c, DateTimeOffset end, CancellationToken ct)
    {
        var movements = await c.Db.StockMovements.AsNoTracking().Where(m => m.At < end)
            .OrderBy(m => m.At).ThenBy(m => m.Id)
            .Select(m => new { m.StockItemId, m.Kind, m.Quantity, m.UnitCost }).ToListAsync(ct);
        var onHand = new Dictionary<Guid, decimal>();
        var average = new Dictionary<Guid, decimal>();
        foreach (var m in movements)
        {
            var have = onHand.GetValueOrDefault(m.StockItemId);
            var avg = average.GetValueOrDefault(m.StockItemId);
            average[m.StockItemId] = m.Kind switch
            {
                StockMovementKind.Opening when m.Quantity > 0 => m.UnitCost,
                StockMovementKind.Delivery => StockBook.AverageAfterDelivery(have, avg, m.Quantity, m.UnitCost),
                StockMovementKind.DeliveryReversal => StockBook.AverageAfterReversal(have, avg, -m.Quantity, m.UnitCost),
                _ => avg,
            };
            onHand[m.StockItemId] = have + m.Quantity;
        }
        return average;
    }
}

/// <summary>
/// Stock variance of one approved count (default: the last): expected, counted and the difference
/// in units and money per item, who counted and approved; totals short, over and net. With an item
/// chosen instead, its variance in money over its last 10 approved counts.
/// </summary>
public sealed class StockVarianceReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("stock-variance", "Stock variance", PermissionKeys.ReportsAll,
        [ReportFilter.Count, ReportFilter.StockItem], UsesPeriod: false);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var items = await c.Db.StockItems.AsNoTracking().ToDictionaryAsync(i => i.Id, ct);
        var locations = await c.Db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        if (f.StockItemId is { } itemId && items.TryGetValue(itemId, out var item))
        {
            var trend = await (from l in c.Db.StockCountLines.AsNoTracking()
                               join k in c.Db.StockCounts.AsNoTracking() on l.CountId equals k.Id
                               where l.StockItemId == itemId && k.Status == StockCountStatus.Approved && l.Counted != null && !k.IsOpening
                               orderby k.AsAt descending
                               select new { k.Id, k.AsAt, k.LocationId, l.Expected, l.Counted, l.UnitCost })
                .Take(10).ToListAsync(ct);
            var rows = trend.OrderBy(t => t.AsAt).Select(t =>
            {
                var diff = (t.Counted ?? 0) - (t.Expected ?? 0);
                return ReportRow.Of(t.AsAt, locations.GetValueOrDefault(t.LocationId, "?"), t.Expected, t.Counted, diff,
                    MoneyMath.Round(diff * (t.UnitCost ?? item.AverageCost)));
            }).ToList();
            var table = new ReportTable($"{item.Name}: the last {rows.Count} counts",
            [
                new("asAt", "Counted at", ColumnType.DateTime), new("location", "Location"), new("expected", "Expected", ColumnType.Quantity),
                new("counted", "Counted", ColumnType.Quantity), new("difference", "Difference", ColumnType.Quantity),
                new("value", "Variance", ColumnType.Money),
            ],
            [new ReportGroup(null, rows)],
            ReportRow.Of("Total", null, null, null, rows.Sum(r => (decimal)r.Cells[4]!), rows.Sum(r => (decimal)r.Cells[5]!)));
            return await Result(c, [table], ct, $"In {item.BaseUnitName}s");
        }

        var count = f.CountId is { } countId
            ? await c.Db.StockCounts.AsNoTracking().FirstOrDefaultAsync(k => k.Id == countId && k.Status == StockCountStatus.Approved, ct)
            : await c.Db.StockCounts.AsNoTracking().Where(k => k.Status == StockCountStatus.Approved && !k.IsOpening)
                .OrderByDescending(k => k.ApprovedAt).FirstOrDefaultAsync(ct);
        if (count is null) return await Result(c, [], ct, "No approved stock count yet");

        var lines = await c.Db.StockCountLines.AsNoTracking().Where(l => l.CountId == count.Id && l.Counted != null).ToListAsync(ct);
        var shown = lines.Where(l => items.ContainsKey(l.StockItemId)).Select(l =>
        {
            var i = items[l.StockItemId];
            var diff = (l.Counted ?? 0) - (l.Expected ?? 0);
            return new { Item = i, l.Expected, l.Counted, Diff = diff, Value = MoneyMath.Round(diff * (l.UnitCost ?? i.AverageCost)) };
        }).ToList();
        var groups = shown.GroupBy(s => s.Item.Category).OrderBy(g => g.Key)
            .Select(g => new ReportGroup(StockOnHandReport.Category(g.Key),
                g.OrderBy(s => s.Item.Name).Select(s => ReportRow.Of(s.Item.Name, s.Item.Describe(s.Expected ?? 0), s.Item.Describe(s.Counted ?? 0),
                    s.Diff == 0 ? "" : s.Item.Describe(s.Diff), s.Diff, s.Value)).ToList(),
                Total: g.Sum(s => s.Value)))
            .ToList();
        var table2 = new ReportTable(null,
        [
            new("item", "Item"), new("expected", "Expected"), new("counted", "Counted"), new("differenceText", "Difference"),
            new("difference", "Base units", ColumnType.Quantity), new("value", "KSh", ColumnType.Money),
        ],
        groups, ReportRow.Of("Net variance", null, null, null, null, shown.Sum(s => s.Value)),
        [
            $"Items short: {shown.Count(s => s.Diff < 0)} (KSh {ReportFormat.Money(shown.Where(s => s.Diff < 0).Sum(s => s.Value))})",
            $"Items over: {shown.Count(s => s.Diff > 0)} (KSh {ReportFormat.Money(shown.Where(s => s.Diff > 0).Sum(s => s.Value))})",
        ]);
        var header = $"{locations.GetValueOrDefault(count.LocationId, "?")} · counted by {c.UserName(count.CountedByUserId)} · " +
                     $"approved by {c.UserName(count.ApprovedByUserId)} · as at {c.Local(count.AsAt):d MMM yyyy HH:mm}";
        return await Result(c, [table2], ct, header);
    }
}

/// <summary>
/// Purchases: deliveries in the period by supplier (date, invoice, location, total, payment,
/// reversed), with each supplier's footer: owed at the start, + deliveries on credit, − payments,
/// = owed at the end. What is owed comes from the Supplier payables lines of the journal, so it
/// always equals the ledger.
/// </summary>
public sealed class PurchasesReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("purchases", "Purchases", PermissionKeys.ReportsAll,
        [ReportFilter.Supplier, ReportFilter.Location]);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var (from, to) = (c.Period.From, c.Period.To);
        var deliveries = await c.Db.Deliveries.AsNoTracking()
            .Where(d => d.Date >= from && d.Date <= to && d.Status == StockDocumentStatus.Posted)
            .Where(d => f.SupplierId == null || d.SupplierId == f.SupplierId)
            .Where(d => f.LocationId == null || d.LocationId == f.LocationId)
            .OrderBy(d => d.Date).ThenBy(d => d.CreatedAt).ToListAsync(ct);
        var suppliers = await c.Db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var locations = await c.Db.StockLocations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var moves = await PayablesMoves(c, to, ct);

        decimal Owed(Guid supplier, DateOnly date) => moves.Where(m => m.Supplier == supplier && m.Date <= date).Sum(m => m.Amount);
        decimal InRange(Guid supplier, string kind) =>
            moves.Where(m => m.Supplier == supplier && m.Kind == kind && m.Date >= from && m.Date <= to).Sum(m => m.Amount);

        var supplierIds = deliveries.Select(d => d.SupplierId)
            .Concat(moves.Where(m => m.Date <= to).GroupBy(m => m.Supplier).Where(g => g.Sum(m => m.Amount) != 0 || g.Any(m => m.Date >= from))
                .Select(g => g.Key))
            .Where(s => f.SupplierId is null || s == f.SupplierId)
            .Distinct().OrderBy(s => suppliers.GetValueOrDefault(s, "?")).ToList();
        var groups = supplierIds.Select(s =>
        {
            var start = Owed(s, from.AddDays(-1));
            var credit = InRange(s, "delivery");
            var opening = InRange(s, "opening");
            var paid = -InRange(s, "payment");
            var end = Owed(s, to);
            var footer = new List<string>
            {
                $"Owed at the start KSh {ReportFormat.Money(start)} + deliveries on credit KSh {ReportFormat.Money(credit)}" +
                (opening != 0 ? $" + opening balance KSh {ReportFormat.Money(opening)}" : "") +
                $" − payments KSh {ReportFormat.Money(paid)} = owed at the end KSh {ReportFormat.Money(end)}",
            };
            var mine = deliveries.Where(d => d.SupplierId == s).ToList();
            return new ReportGroup(suppliers.GetValueOrDefault(s, "?"),
                mine.Select(d => ReportRow.Of(d.Date, d.InvoiceNumber, locations.GetValueOrDefault(d.LocationId, "?"), d.Total, Payment(d.Payment),
                    d.ReversalStatus == StockDocumentStatus.Posted ? "Reversed" : null)).ToList(),
                null, footer, Total: mine.Where(d => d.ReversalStatus != StockDocumentStatus.Posted).Sum(d => d.Total));
        }).ToList();
        var table = new ReportTable(null,
        [
            new("date", "Date", ColumnType.Date), new("invoice", "Invoice"), new("location", "Location"), new("total", "Total", ColumnType.Money),
            new("payment", "Payment"), new("reversed", "Reversed"),
        ],
        groups, ReportRow.Of("Total", null, null, deliveries.Where(d => d.ReversalStatus != StockDocumentStatus.Posted).Sum(d => d.Total), null, null),
        [$"Owed to suppliers at the end: KSh {ReportFormat.Money(supplierIds.Sum(s => Owed(s, to)))}"]);
        return await Result(c, [table], ct);
    }

    private static string Payment(DeliveryPayment payment) => payment switch
    {
        DeliveryPayment.OnCredit => "On credit",
        DeliveryPayment.Mpesa => "M-Pesa",
        _ => payment.ToString(),
    };

    /// <param name="Detail">How the supplier statement names it: "Invoice INV-204", "Bank · ref 7781".</param>
    /// <param name="Reversal">The reversal of an earlier delivery, payment or opening balance.</param>
    /// <param name="At">When it was posted, to order a day's moves.</param>
    public sealed record PayablesMove(
        Guid Supplier, DateOnly Date, string Kind, decimal Amount, string Detail = "", bool Reversal = false, DateTimeOffset At = default);

    /// <summary>
    /// Every change to what suppliers are owed up to a date, from the journal: the Supplier payables
    /// lines of each delivery and payment (and their reversals) by their source document, and the
    /// suppliers' opening balances (taken back when that entry is reversed). Credit is owed (+).
    /// </summary>
    public static async Task<List<PayablesMove>> PayablesMoves(ReportContext c, DateOnly to, CancellationToken ct)
    {
        var payables = await c.Db.LedgerAccounts.AsNoTracking().Where(a => a.Code == LedgerAccountCodes.SupplierPayables)
            .Select(a => a.Id).FirstOrDefaultAsync(ct);
        var lines = await (from l in c.Db.JournalLines.AsNoTracking()
                           join e in c.Db.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.Id
                           where l.AccountId == payables && e.BusinessDate <= to
                           select new { e.SourceType, e.SourceId, e.BusinessDate, e.CreatedAt, l.Credit, l.Debit })
            .ToListAsync(ct);
        var deliveries = await c.Db.Deliveries.AsNoTracking().ToDictionaryAsync(d => d.Id, d => new { d.SupplierId, d.InvoiceNumber }, ct);
        var documents = await c.Db.MoneyDocuments.AsNoTracking()
            .Where(d => d.Kind == MoneyDocumentKind.SupplierPayment || d.Kind == MoneyDocumentKind.OpeningBalances)
            .ToDictionaryAsync(d => d.Id, ct);
        var moves = new List<PayablesMove>();
        foreach (var entry in lines.GroupBy(l => (l.SourceType, l.SourceId, l.BusinessDate)))
        {
            var (source, sourceId, date) = entry.Key;
            if (date is not { } day || sourceId is null || !Guid.TryParse(sourceId.Split(':')[0], out var id)) continue;
            var amount = entry.Sum(l => l.Credit - l.Debit);
            var reversal = sourceId.EndsWith(":reversal", StringComparison.Ordinal);
            var at = entry.Min(l => l.CreatedAt);
            if (source == DeliveryCommands.LedgerSource && deliveries.TryGetValue(id, out var delivery))
                moves.Add(new PayablesMove(delivery.SupplierId, day, "delivery", amount,
                    string.IsNullOrWhiteSpace(delivery.InvoiceNumber) ? "" : $"Invoice {delivery.InvoiceNumber}", reversal, at));
            else if (source == MoneyActions.LedgerSource && documents.TryGetValue(id, out var doc))
            {
                if (doc.Kind == MoneyDocumentKind.SupplierPayment && doc.SupplierId is { } paid)
                {
                    var from = doc.From is { } account ? MoneyActions.AccountName(account) : "";
                    var detail = string.IsNullOrWhiteSpace(doc.Reference) ? from : $"{from} · ref {doc.Reference}";
                    moves.Add(new PayablesMove(paid, day, "payment", amount, detail, reversal, at));
                }
                else
                {
                    var sign = reversal ? -1 : 1;
                    moves.AddRange(doc.OpeningLines.Where(o => o.SupplierId is not null)
                        .Select(o => new PayablesMove(o.SupplierId!.Value, day, "opening", sign * o.Amount, "", reversal, at)));
                }
            }
        }
        return moves;
    }
}
