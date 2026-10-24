using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Cash;
using Tausi.Domain.Service;

namespace Tausi.Application.Day;

public sealed record VatRateDto(string Label, decimal Net, decimal Vat);

public sealed record PaymentTotalDto(string Type, int Count, decimal Amount);

public sealed record ShiftDifferenceDto(string Cashier, string Terminal, decimal Difference);

/// <summary>
/// A day in figures, from its settled tickets (sales by the shared <see cref="Reports.SalesQuery"/>): gross sales (gifts at 0), discounts, gifts at their
/// original value and voids (both for information), net sales and VAT by rate, payments by type,
/// rounding, each shift's cash-up difference and the pay-outs. It adds up exactly:
/// Gross − Discounts = Net + VAT, and Gross − Discounts + Rounding = the payments.
/// </summary>
public sealed record DaySummaryDto(
    int Tickets, decimal Gross, decimal Discounts, decimal Gifts, decimal Voids, decimal Net, decimal Vat,
    IReadOnlyList<VatRateDto> VatRates, IReadOnlyList<PaymentTotalDto> Payments, decimal PaymentsTotal, decimal Rounding,
    IReadOnlyList<ShiftDifferenceDto> ShiftDifferences, decimal PayOuts, Money.DailySummaryDto? Profit = null);

public sealed class DaySummaries(ITausiDb db)
{
    public async Task<DaySummaryDto> For(Guid workPeriodId, CancellationToken ct)
    {
        // Sales come from the one shared definition (Reports.SalesQuery): settled tickets, voids and
        // cancelled lines out, gifts at 0 with their value apart, rounding on its own.
        Guid[] period = [workPeriodId];
        var lines = await Reports.SalesQuery.Lines(db, period, includeOpen: false)
            .Select(l => new { l.Gross, l.Discount, l.Net, l.Vat, l.VatRate, l.VatExempt, l.IsGift, l.Quantity, l.UnitPrice })
            .ToListAsync(ct);
        var settled = await db.Tickets.AsNoTracking().CountAsync(k => k.WorkPeriodId == workPeriodId && k.Status == TicketStatus.Settled, ct);
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.WorkPeriodId == workPeriodId && p.Status == PaymentStatus.Final)
            .Select(p => new { p.Type, p.Amount }).ToListAsync(ct);
        var allIds = db.Tickets.AsNoTracking().Where(k => k.WorkPeriodId == workPeriodId).Select(k => k.Id);
        var voids = await db.VoidLogs.AsNoTracking().Where(v => allIds.Contains(v.TicketId)).Select(v => v.Amount).ToListAsync(ct);
        var gifts = lines.Where(l => l.IsGift).Select(l => l.Quantity * l.UnitPrice).ToList();
        var rounding = await Reports.SalesQuery.Rounding(db, period, ct);
        var shifts = await db.CashierShifts.AsNoTracking()
            .Where(s => s.WorkPeriodId == workPeriodId && s.Status == ShiftStatus.Closed)
            .Select(s => new
            {
                Cashier = db.Users.Where(u => u.Id == s.CashierUserId).Select(u => u.Name).FirstOrDefault(),
                Terminal = db.Terminals.Where(t => t.Id == s.TerminalId).Select(t => t.Name).FirstOrDefault(),
                s.Difference,
                s.OpenedAt,
            }).ToListAsync(ct);
        var payOuts = await db.DrawerMovements.AsNoTracking()
            .Where(m => m.WorkPeriodId == workPeriodId && m.Kind == DrawerMovementKind.PayOut).Select(m => m.Amount).ToListAsync(ct);

        var rates = lines.GroupBy(l => (l.VatRate, l.VatExempt)).OrderByDescending(g => g.Key.VatRate).ThenBy(g => g.Key.VatExempt)
            .Select(g => new VatRateDto(g.Key.VatExempt ? "VAT exempt" : $"VAT {g.Key.VatRate:0.##}%", g.Sum(l => l.Net), g.Sum(l => l.Vat)))
            .ToList();
        var byType = payments.GroupBy(p => p.Type).OrderBy(g => g.Key)
            .Select(g => new PaymentTotalDto(g.Key == PaymentType.Mpesa ? "M-Pesa" : g.Key.ToString(), g.Count(), g.Sum(p => p.Amount)))
            .ToList();
        return new DaySummaryDto(
            settled, lines.Sum(l => l.Gross), lines.Sum(l => l.Discount), gifts.Sum(), voids.Sum(),
            lines.Sum(l => l.Net), lines.Sum(l => l.Vat), rates, byType, payments.Sum(p => p.Amount), rounding,
            shifts.OrderBy(s => s.OpenedAt).Select(s => new ShiftDifferenceDto(s.Cashier ?? "?", s.Terminal ?? "?", s.Difference ?? 0)).ToList(),
            payOuts.Sum(),
            await Money.BusinessDates.OfPeriod(db, workPeriodId, ct) is { } date ? await Money.Summaries.For(db, date, includeLate: false, ct) : null);
    }
}
