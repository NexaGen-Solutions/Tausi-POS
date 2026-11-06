using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Domain.Cash;
using Tausi.Domain.Service;

namespace Tausi.Application.Day;

public sealed record VatRateDto(string Label, decimal Net, decimal Vat);

public sealed record PaymentTotalDto(string Type, int Count, decimal Amount);

public sealed record ShiftDifferenceDto(string Cashier, string Terminal, decimal Difference);

/// <summary>One calendar date of a work period: its sales (by the date served) and what was taken (by the date paid).</summary>
public sealed record DayDateDto(DateOnly Date, int Items, decimal Sales, decimal Vat, decimal Taken);

/// <summary>
/// A day in figures, from its settled tickets (sales by the shared <see cref="Reports.SalesQuery"/>): gross sales (gifts at 0), discounts, gifts at their
/// original value and voids (both for information), net sales and VAT by rate, payments by type,
/// rounding, each shift's cash-up difference and the pay-outs. It adds up exactly:
/// Gross − Discounts = Net + VAT, and Gross − Discounts + Rounding = the payments.
/// A work period that ran over several dates also has its figures per date (<see cref="Dates"/>),
/// with <see cref="Spans"/> saying so; the profit from the books is per date, so it's only given
/// for a work period of one date.
/// </summary>
public sealed record DaySummaryDto(
    int Tickets, decimal Gross, decimal Discounts, decimal Gifts, decimal Voids, decimal Net, decimal Vat,
    IReadOnlyList<VatRateDto> VatRates, IReadOnlyList<PaymentTotalDto> Payments, decimal PaymentsTotal, decimal Rounding,
    IReadOnlyList<ShiftDifferenceDto> ShiftDifferences, decimal PayOuts, Money.DailySummaryDto? Profit = null,
    IReadOnlyList<DayDateDto>? Dates = null, string? Spans = null);

public sealed class DaySummaries(ITausiDb db, TimeProvider clock)
{
    /// <summary>"This work period covered 3 days (Sun 2 – Tue 4 Nov)."; null for one date.</summary>
    public static string? SpanWarning(DateOnly from, DateOnly to)
    {
        var days = to.DayNumber - from.DayNumber + 1;
        if (days < 2) return null;
        var start = from.Month == to.Month ? from.ToString("ddd d", System.Globalization.CultureInfo.InvariantCulture)
            : from.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);
        return $"This work period covered {days} days ({start} – {to.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture)}).";
    }

    public async Task<DaySummaryDto> For(Guid workPeriodId, CancellationToken ct)
    {
        // Sales come from the one shared definition (Reports.SalesQuery): settled tickets, voids and
        // cancelled lines out, gifts at 0 with their value apart, rounding on its own.
        Guid[] period = [workPeriodId];
        var lines = await Reports.SalesQuery.Lines(db, period, includeOpen: false)
            .Select(l => new { l.Gross, l.Discount, l.Net, l.Vat, l.VatRate, l.VatExempt, l.IsGift, l.Quantity, l.UnitPrice, l.ServedAt })
            .ToListAsync(ct);
        var settled = await db.Tickets.AsNoTracking().CountAsync(k => k.WorkPeriodId == workPeriodId && k.Status == TicketStatus.Settled, ct);
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.WorkPeriodId == workPeriodId && p.Status == PaymentStatus.Final)
            .Select(p => new { p.Type, p.Amount, p.TakenAt }).ToListAsync(ct);
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

        var zone = await Money.BusinessDates.Zone(db, ct);
        var times = await db.WorkPeriods.AsNoTracking().Where(p => p.Id == workPeriodId)
            .Select(p => new { p.OpenedAt, p.ClosedAt }).FirstAsync(ct);
        var (first, last) = (Money.BusinessDates.Of(times.OpenedAt, zone), Money.BusinessDates.Of(times.ClosedAt ?? clock.GetUtcNow(), zone));
        var dates = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(first.AddDays).Select(date =>
        {
            var served = lines.Where(l => Money.BusinessDates.Of(l.ServedAt, zone) == date).ToList();
            return new DayDateDto(date, served.Count, served.Sum(l => l.Gross - l.Discount), served.Sum(l => l.Vat),
                payments.Where(p => Money.BusinessDates.Of(p.TakenAt, zone) == date).Sum(p => p.Amount));
        }).ToList();
        var oneDate = first == last;
        return new DaySummaryDto(
            settled, lines.Sum(l => l.Gross), lines.Sum(l => l.Discount), gifts.Sum(), voids.Sum(),
            lines.Sum(l => l.Net), lines.Sum(l => l.Vat), rates, byType, payments.Sum(p => p.Amount), rounding,
            shifts.OrderBy(s => s.OpenedAt).Select(s => new ShiftDifferenceDto(s.Cashier ?? "?", s.Terminal ?? "?", s.Difference ?? 0)).ToList(),
            payOuts.Sum(),
            oneDate ? await Money.Summaries.For(db, first, includeLate: false, ct) : null,
            oneDate ? [] : dates, SpanWarning(first, last));
    }
}
