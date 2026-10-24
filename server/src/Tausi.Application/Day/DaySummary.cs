using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Cash;
using Tausi.Domain.Service;

namespace Tausi.Application.Day;

public sealed record VatRateDto(string Label, decimal Net, decimal Vat);

public sealed record PaymentTotalDto(string Type, int Count, decimal Amount);

public sealed record ShiftDifferenceDto(string Cashier, string Terminal, decimal Difference);

/// <summary>
/// A day in figures, from its settled tickets: gross sales (gifts at 0), discounts, gifts at their
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
        var tickets = await db.Tickets.AsNoTracking().Include(k => k.Lines).Include(k => k.Payments)
            .Where(k => k.WorkPeriodId == workPeriodId && k.Status == TicketStatus.Settled).ToListAsync(ct);
        var ids = tickets.Select(k => k.Id).ToList();
        var allIds = await db.Tickets.AsNoTracking().Where(k => k.WorkPeriodId == workPeriodId).Select(k => k.Id).ToListAsync(ct);
        var lines = tickets.SelectMany(k => k.Lines).Where(l => l.Counts).ToList();
        var payments = tickets.SelectMany(k => k.Payments).Where(p => p.Status == PaymentStatus.Final).ToList();
        var gifts = await db.GiftLogs.AsNoTracking().Where(g => ids.Contains(g.TicketId)).Select(g => g.Amount).ToListAsync(ct);
        var voids = await db.VoidLogs.AsNoTracking().Where(v => allIds.Contains(v.TicketId)).Select(v => v.Amount).ToListAsync(ct);
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
            tickets.Count, lines.Sum(l => l.Gross), lines.Sum(l => l.Discount), gifts.Sum(), voids.Sum(),
            lines.Sum(l => l.Net), lines.Sum(l => l.Vat), rates, byType, payments.Sum(p => p.Amount), tickets.Sum(k => k.RoundingAmount),
            shifts.OrderBy(s => s.OpenedAt).Select(s => new ShiftDifferenceDto(s.Cashier ?? "?", s.Terminal ?? "?", s.Difference ?? 0)).ToList(),
            payOuts.Sum(),
            await Money.BusinessDates.OfPeriod(db, workPeriodId, ct) is { } date ? await Money.Summaries.For(db, date, includeLate: false, ct) : null);
    }
}
