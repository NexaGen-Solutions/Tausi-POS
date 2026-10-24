using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Stock;
using Tausi.Domain.Service;
using Tausi.Domain.Stock;

namespace Tausi.Application.Reports;

/// <summary>One line that counts as a sale, with its ticket's facts (a projection; never an entity).</summary>
public sealed class SaleLine
{
    public Guid LineId { get; init; }
    public Guid SaleLineId { get; init; }
    public Guid TicketId { get; init; }
    public long TicketNumber { get; init; }
    public TicketStatus TicketStatus { get; init; }
    public Guid WorkPeriodId { get; init; }
    public Guid? MenuItemId { get; init; }
    public string Name { get; init; } = "";
    public string? Options { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    /// <summary>Quantity × price, VAT included; 0 for a gift.</summary>
    public decimal Gross { get; init; }
    /// <summary>The line's share of the ticket discount.</summary>
    public decimal Discount { get; init; }
    public decimal Net { get; init; }
    public decimal Vat { get; init; }
    public decimal VatRate { get; init; }
    public bool VatExempt { get; init; }
    public bool IsGift { get; init; }
    public decimal? OriginalUnitPrice { get; init; }
    public Guid AddedByUserId { get; init; }
    public Guid OpenedByUserId { get; init; }
    public Guid AddedTerminalId { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public int? PrintRound { get; init; }

    /// <summary>The sale: gross after the discount share (0 for a gift).</summary>
    public decimal Amount => Gross - Discount;

    /// <summary>What a gift would have cost: quantity × its price.</summary>
    public decimal GiftValue => IsGift ? Quantity * UnitPrice : 0;

    public Guid CreditedTo(CreditTo credit) => credit == CreditTo.OpenedBy ? OpenedByUserId : AddedByUserId;
}

/// <summary>
/// The one definition of "sales", used by the reports, the Z report and My sales: lines on settled
/// tickets (and open ones when asked), voided and cancelled lines left out, each line's amount its
/// gross after its discount share, gifts at 0 (their value is <see cref="SaleLine.GiftValue"/>).
/// Rounding is a figure of its own (<see cref="Rounding"/>). Merged tickets' lines are on the
/// ticket they were merged into; cancelled tickets have no sales.
/// </summary>
public static class SalesQuery
{
    public static IQueryable<SaleLine> Lines(ITausiDb db, IReadOnlyCollection<Guid> periodIds, bool includeOpen) =>
        from l in db.OrderLines.AsNoTracking()
        join k in db.Tickets.AsNoTracking() on l.TicketId equals k.Id
        where periodIds.Contains(k.WorkPeriodId)
              && (k.Status == TicketStatus.Settled || (includeOpen && k.Status == TicketStatus.Open))
              && l.Status != OrderLineStatus.Cancelled && l.Status != OrderLineStatus.Voided && l.VoidedAt == null
        select new SaleLine
        {
            LineId = l.Id,
            SaleLineId = l.SplitFromLineId ?? l.Id,
            TicketId = k.Id,
            TicketNumber = k.Number,
            TicketStatus = k.Status,
            WorkPeriodId = k.WorkPeriodId,
            MenuItemId = l.MenuItemId,
            Name = l.Name,
            Options = l.Options,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            Gross = l.Gross,
            Discount = l.Discount,
            Net = l.Net,
            Vat = l.Vat,
            VatRate = l.VatRate,
            VatExempt = l.VatExempt,
            IsGift = l.IsGift,
            OriginalUnitPrice = l.OriginalUnitPrice,
            AddedByUserId = l.AddedByUserId,
            OpenedByUserId = k.OpenedByUserId,
            AddedTerminalId = l.AddedTerminalId,
            AddedAt = l.AddedAt,
            PrintRound = l.PrintRound,
        };

    /// <summary>The rounding on settled tickets (open tickets aren't rounded yet).</summary>
    public static async Task<decimal> Rounding(ITausiDb db, IReadOnlyCollection<Guid> periodIds, CancellationToken ct) =>
        await db.Tickets.AsNoTracking()
            .Where(k => periodIds.Contains(k.WorkPeriodId) && k.Status == TicketStatus.Settled)
            .SumAsync(k => (decimal?)k.RoundingAmount, ct) ?? 0;

    /// <summary>
    /// Cost of sales per sale line from the stock movements of its print (Task 11), shared between
    /// the parts a line was split into by their quantities. Lines not yet printed have none.
    /// Summed in the database: one query, however many lines.
    /// </summary>
    public static async Task<Dictionary<Guid, decimal>> CostPerLine(ITausiDb db, IQueryable<SaleLine> lines, CancellationToken ct)
    {
        var rows = await (
                from l in lines
                join m in db.StockMovements.AsNoTracking() on (Guid?)l.SaleLineId equals m.ReferenceId
                where m.Kind == StockMovementKind.Sale && m.ReferenceType == StockSelling.SaleReference && m.LineQuantity > 0
                group new { l.Quantity, m.UnitCost, Moved = m.Quantity, m.LineQuantity } by l.LineId
                into g
                select new { LineId = g.Key, Cost = g.Sum(x => -x.Moved * x.UnitCost * x.Quantity / x.LineQuantity!.Value) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.LineId, r => Domain.Money.MoneyMath.Round(r.Cost));
    }
}
