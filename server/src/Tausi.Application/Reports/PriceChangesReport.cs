using Microsoft.EntityFrameworkCore;
using Tausi.Domain.Menu;
using Tausi.Domain.People;

namespace Tausi.Application.Reports;

/// <summary>
/// Price changes: menu prices changed on the admin site or by import (item, price list, old, new,
/// who, when), and prices changed on tickets (ticket, item, old, new, quantity, the difference in
/// money, who, approved by, when).
/// </summary>
public sealed class PriceChangesReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("price-changes", "Price changes", PermissionKeys.ReportsAll,
        [ReportFilter.Kind, ReportFilter.User], Kinds: ["Menu", "Ticket"]);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var tables = new List<ReportTable>();
        if (f.Kind is null or "" or "Menu")
        {
            var menu = await (from p in c.Db.PriceChangeLogs.AsNoTracking()
                              where p.Kind == PriceChangeKind.Menu && p.At >= c.Period.Start && p.At <= c.Period.End
                                    && (f.UserId == null || p.ChangedByUserId == f.UserId)
                              orderby p.At
                              select new
                              {
                                  p.At, p.OldPrice, p.NewPrice, p.ChangedByUserId, p.Source,
                                  Item = c.Db.MenuItems.Where(m => m.Id == p.MenuItemId).Select(m => m.Name).FirstOrDefault()
                                         ?? c.Db.ModifierOptions.Where(o => o.Id == p.ModifierOptionId).Select(o => o.Name + " (option)").FirstOrDefault(),
                                  List = c.Db.PriceLists.Where(l => l.Id == p.PriceListId).Select(l => l.Name).FirstOrDefault(),
                              }).ToListAsync(ct);
            tables.Add(new ReportTable("Menu price changes",
            [
                new("item", "Item"), new("list", "Price list"), new("old", "Old", ColumnType.Money), new("new", "New", ColumnType.Money),
                new("who", "Changed by"), new("source", "How"), new("when", "When", ColumnType.DateTime),
            ],
            [new ReportGroup(null, menu.Select(m => ReportRow.Of(m.Item ?? "(deleted)", m.List, m.OldPrice, m.NewPrice, c.UserName(m.ChangedByUserId),
                m.Source == PriceChangeSources.Import ? "Import" : "Admin site", m.At)).ToList())]));
        }
        if (f.Kind is null or "" or "Ticket")
        {
            var ids = c.Period.PeriodIds;
            var onTickets = await (from p in c.Db.PriceChangeLogs.AsNoTracking()
                                   join k in c.Db.Tickets.AsNoTracking() on p.TicketId equals k.Id
                                   join l in c.Db.OrderLines.AsNoTracking() on p.OrderLineId equals l.Id
                                   where p.Kind == PriceChangeKind.Ticket && ids.Contains(k.WorkPeriodId)
                                         && (f.UserId == null || p.ChangedByUserId == f.UserId)
                                   orderby p.At
                                   select new { p.At, k.Id, k.Number, l.Name, l.Quantity, p.OldPrice, p.NewPrice, p.ChangedByUserId, p.ApprovedByUserId })
                .ToListAsync(ct);
            decimal Difference(decimal? oldPrice, decimal? newPrice, decimal quantity) => ((newPrice ?? 0) - (oldPrice ?? 0)) * quantity;
            tables.Add(new ReportTable("On-ticket price changes",
            [
                new("ticket", "Ticket", ColumnType.Ticket), new("item", "Item"), new("old", "Old", ColumnType.Money), new("new", "New", ColumnType.Money),
                new("qty", "Qty", ColumnType.Quantity), new("difference", "Difference", ColumnType.Money), new("who", "Changed by"),
                new("approved", "Approved by"), new("when", "When", ColumnType.DateTime),
            ],
            [new ReportGroup(null, onTickets.Select(p => new ReportRow([p.Number, p.Name, p.OldPrice, p.NewPrice, p.Quantity,
                Difference(p.OldPrice, p.NewPrice, p.Quantity), c.UserName(p.ChangedByUserId), c.UserName(p.ApprovedByUserId), p.At], p.Id)).ToList())],
            ReportRow.Of("Total", null, null, null, null, onTickets.Sum(p => Difference(p.OldPrice, p.NewPrice, p.Quantity)), null, null, null)));
        }
        return await Result(c, tables, ct);
    }
}
