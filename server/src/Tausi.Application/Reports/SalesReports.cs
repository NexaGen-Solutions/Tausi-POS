using Microsoft.EntityFrameworkCore;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Reports;

/// <summary>The voids on a period's tickets, with who each line is credited to.</summary>
internal static class VoidQueries
{
    public sealed record VoidRow(Guid? MenuItemId, string Name, decimal Quantity, decimal Amount, Guid AddedBy, Guid OpenedBy, Guid Terminal);

    public static Task<List<VoidRow>> ForPeriod(ReportContext c, CancellationToken ct)
    {
        var ids = c.Period.PeriodIds;
        return (from v in c.Db.VoidLogs.AsNoTracking()
                join k in c.Db.Tickets.AsNoTracking() on v.TicketId equals k.Id
                join l in c.Db.OrderLines.AsNoTracking() on v.OrderLineId equals l.Id
                where ids.Contains(k.WorkPeriodId) && k.Status != TicketStatus.Cancelled
                select new VoidRow(v.MenuItemId, v.ItemName, v.Quantity, v.Amount, l.AddedByUserId, k.OpenedByUserId, l.AddedTerminalId))
            .ToListAsync(ct);
    }
}

/// <summary>
/// Sales by user (wireframe 19): a group per user (name, role, total) with each line sold (ticket,
/// time, item with its options, quantity, amount), and a footer with their tickets, gifts, voids
/// and the discounts on their lines. Sales are credited to who added the item or who opened the ticket.
/// </summary>
public sealed class SalesByUserReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("sales-by-user", "Sales by user", PermissionKeys.ReportsAll,
        [ReportFilter.User, ReportFilter.Terminal, ReportFilter.CreditTo, ReportFilter.IncludeOpen], Receipt: true);

    public static readonly IReadOnlyList<ReportColumn> Columns =
    [
        new("ticket", "Ticket", ColumnType.Ticket), new("time", "Time", ColumnType.Time), new("item", "Item"),
        new("qty", "Qty", ColumnType.Quantity), new("amount", "Amount", ColumnType.Money),
    ];

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var query = SalesQuery.Lines(c.Db, c.Period.PeriodIds, c.IncludeOpen);
        if (f.TerminalId is { } terminal) query = query.Where(l => l.AddedTerminalId == terminal);
        if (f.UserId is { } user)
            query = f.CreditTo == CreditTo.OpenedBy ? query.Where(l => l.OpenedByUserId == user) : query.Where(l => l.AddedByUserId == user);
        var lines = await query.ToListAsync(ct);
        var voids = (await VoidQueries.ForPeriod(c, ct))
            .Where(v => f.TerminalId is null || v.Terminal == f.TerminalId)
            .Select(v => (User: f.CreditTo == CreditTo.OpenedBy ? v.OpenedBy : v.AddedBy, v.Amount))
            .Where(v => f.UserId is null || v.User == f.UserId).ToList();

        var groups = lines.GroupBy(l => l.CreditedTo(f.CreditTo))
            .Select(g => (User: g.Key, Lines: g.ToList(), Total: g.Sum(l => l.Amount)))
            .OrderByDescending(g => g.Total).ThenBy(g => c.UserName(g.User))
            .Select(g =>
            {
                var gifts = g.Lines.Where(l => l.IsGift).ToList();
                var mine = voids.Where(v => v.User == g.User).ToList();
                var footer = $"Tickets {g.Lines.Select(l => l.TicketId).Distinct().Count()} · Gifts {gifts.Count} " +
                             $"(KSh {ReportFormat.Money(gifts.Sum(l => l.GiftValue))}) · Voids {mine.Count} (KSh {ReportFormat.Money(mine.Sum(v => v.Amount))}) · " +
                             $"Discounts KSh {ReportFormat.Money(g.Lines.Sum(l => l.Discount))}";
                var rows = g.Lines.OrderBy(l => l.AddedAt).ThenBy(l => l.TicketNumber).ThenBy(l => l.Name).ThenBy(l => l.IsGift).ThenBy(l => l.Quantity)
                    .Select(l => new ReportRow([l.TicketNumber, l.AddedAt, Item(l), l.Quantity, l.Amount], l.TicketId, Muted: l.IsGift))
                    .ToList();
                return new ReportGroup(c.UserName(g.User), rows, null, [footer], c.RoleOf(g.User), g.Total);
            }).ToList();

        var total = lines.Sum(l => l.Amount);
        var table = new ReportTable(null, Columns, groups, ReportRow.Of("Total", null, null, lines.Sum(l => l.Quantity), total));
        var rounding = await RoundingNote(c, ct);
        return await Result(c, [table], ct, await OpenNote(c, ct), rounding);
    }

    /// <summary>"Spirits Tot (Double, Cold)"; gifts say so.</summary>
    public static string Item(SaleLine l) =>
        l.Name + (string.IsNullOrEmpty(l.Options) ? "" : $" ({l.Options})") + (l.IsGift ? $" · gift, KSh {ReportFormat.Money(l.GiftValue)}" : "");

    public static async Task<string?> RoundingNote(ReportContext c, CancellationToken ct)
    {
        var rounding = await SalesQuery.Rounding(c.Db, c.Period.PeriodIds, ct);
        return rounding == 0 ? null : $"Rounding on receipts: KSh {ReportFormat.Money(rounding)} (not in the item amounts)";
    }
}

/// <summary>
/// Sales by item, grouped by menu category: quantity, gross, discount, net of VAT, VAT, cost of
/// sales (from the stock movements of each print), gross profit and margin; gift and void
/// quantities. Items without a recipe say "No recipe" instead of a cost.
/// </summary>
public sealed class SalesByItemReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("sales-by-item", "Sales by item", PermissionKeys.ReportsAll,
        [ReportFilter.Category, ReportFilter.Terminal, ReportFilter.User, ReportFilter.IncludeOpen]);

    private static readonly IReadOnlyList<ReportColumn> Columns =
    [
        new("item", "Item"), new("qty", "Qty", ColumnType.Quantity), new("gross", "Gross", ColumnType.Money),
        new("discount", "Discount", ColumnType.Money), new("net", "Net of VAT", ColumnType.Money), new("vat", "VAT", ColumnType.Money),
        new("cost", "Cost of sales", ColumnType.Money), new("profit", "Gross profit", ColumnType.Money),
        new("margin", "Margin", ColumnType.Percent), new("gifts", "Gifts", ColumnType.Quantity), new("voids", "Voids", ColumnType.Quantity),
    ];

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var query = SalesQuery.Lines(c.Db, c.Period.PeriodIds, c.IncludeOpen);
        if (f.TerminalId is { } terminal) query = query.Where(l => l.AddedTerminalId == terminal);
        if (f.UserId is { } user) query = query.Where(l => l.AddedByUserId == user);
        if (f.CategoryId is { } category) query = query.Where(l => c.Db.MenuItems.Any(m => m.Id == l.MenuItemId && m.CategoryId == category));
        var lines = await query.ToListAsync(ct);
        var costs = await SalesQuery.CostPerLine(c.Db, query, ct);
        var items = await c.Db.MenuItems.AsNoTracking()
            .Select(m => new { m.Id, m.CategoryId, HasRecipe = c.Db.RecipeLines.Any(r => r.MenuItemId == m.Id) })
            .ToDictionaryAsync(m => m.Id, ct);
        var categories = await c.Db.Categories.AsNoTracking().ToDictionaryAsync(x => x.Id, x => (x.Name, x.SortOrder), ct);
        var voids = (await VoidQueries.ForPeriod(c, ct))
            .Where(v => (f.TerminalId is null || v.Terminal == f.TerminalId) && (f.UserId is null || v.AddedBy == f.UserId))
            .ToList();

        (string Name, int Order) CategoryOf(Guid? menuItem) =>
            menuItem is { } id && items.TryGetValue(id, out var m) && categories.TryGetValue(m.CategoryId, out var cat) ? cat : ("Other", int.MaxValue);

        var rows = lines.GroupBy(l => (l.MenuItemId, Key: l.MenuItemId is null ? l.Name : ""))
            .Select(g =>
            {
                var recipe = g.Key.MenuItemId is { } id && items.TryGetValue(id, out var m) && m.HasRecipe;
                var cost = recipe ? g.Sum(l => costs.GetValueOrDefault(l.LineId)) : (decimal?)null;
                var net = g.Sum(l => l.Net);
                var profit = cost is { } x ? net - x : (decimal?)null;
                var voided = voids.Where(v => v.MenuItemId == g.Key.MenuItemId && (g.Key.MenuItemId != null || v.Name == g.Key.Key))
                    .Sum(v => v.Quantity);
                return new
                {
                    Category = CategoryOf(g.Key.MenuItemId),
                    Name = g.OrderByDescending(l => l.AddedAt).First().Name,
                    Qty = g.Sum(l => l.Quantity), Gross = g.Sum(l => l.Gross), Discount = g.Sum(l => l.Discount), Net = net, Vat = g.Sum(l => l.Vat),
                    Cost = cost, Profit = profit, Gifts = g.Where(l => l.IsGift).Sum(l => l.Quantity), Voids = voided,
                };
            }).ToList();

        object? Margin(decimal? profit, decimal net) => profit is { } p && net != 0 ? Domain.Money.MoneyMath.Round(p / net * 100, 1) : null;

        var groups = rows.GroupBy(r => r.Category).OrderBy(g => g.Key.Order).ThenBy(g => g.Key.Name)
            .Select(g =>
            {
                var list = g.OrderByDescending(r => r.Gross).ThenBy(r => r.Name).ToList();
                var cost = list.Where(r => r.Cost is not null).Sum(r => r.Cost!.Value);
                var profit = list.Where(r => r.Profit is not null).Sum(r => r.Profit!.Value);
                var costedNet = list.Where(r => r.Profit is not null).Sum(r => r.Net);
                return new ReportGroup(g.Key.Name,
                    list.Select(r => ReportRow.Of(r.Name, r.Qty, r.Gross, r.Discount, r.Net, r.Vat, (object?)r.Cost ?? "No recipe", r.Profit,
                        Margin(r.Profit, r.Net), r.Gifts, r.Voids)).ToList(),
                    ReportRow.Of($"{g.Key.Name} total", list.Sum(r => r.Qty), list.Sum(r => r.Gross), list.Sum(r => r.Discount), list.Sum(r => r.Net),
                        list.Sum(r => r.Vat), cost, profit, Margin(profit, costedNet), list.Sum(r => r.Gifts), list.Sum(r => r.Voids)),
                    Total: list.Sum(r => r.Gross - r.Discount));
            }).ToList();

        var allCost = rows.Where(r => r.Cost is not null).Sum(r => r.Cost!.Value);
        var allProfit = rows.Where(r => r.Profit is not null).Sum(r => r.Profit!.Value);
        var totals = ReportRow.Of("Total", rows.Sum(r => r.Qty), rows.Sum(r => r.Gross), rows.Sum(r => r.Discount), rows.Sum(r => r.Net),
            rows.Sum(r => r.Vat), allCost, allProfit, Margin(allProfit, rows.Where(r => r.Profit is not null).Sum(r => r.Net)),
            rows.Sum(r => r.Gifts), rows.Sum(r => r.Voids));
        var noRecipe = rows.Count(r => r.Cost is null);
        return await Result(c, [new ReportTable(null, Columns, groups, totals)], ct,
            await OpenNote(c, ct),
            noRecipe == 0 ? null : $"{noRecipe} item{(noRecipe == 1 ? " has" : "s have")} no recipe: their cost of sales and profit are not in the totals",
            await SalesByUserReport.RoundingNote(c, ct));
    }
}
