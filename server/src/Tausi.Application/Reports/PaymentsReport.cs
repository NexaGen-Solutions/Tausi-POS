using Microsoft.EntityFrameworkCore;
using Tausi.Domain.Cash;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Reports;

/// <summary>
/// Payments and cash-ups: each cashier shift (float, cash / M-Pesa / card taken, pay-outs, drops,
/// expected, counted, difference, who signed off), what each waiter brought in and their open
/// bills, and every M-Pesa code (with the payments that have none) for checking the till statement.
/// Payments count once the ticket is settled.
/// </summary>
public sealed class PaymentsReport : ReportBase
{
    public override ReportDefinition Definition { get; } = new("payments", "Payments and cash-ups", PermissionKeys.ReportsAll,
        [ReportFilter.Cashier, ReportFilter.Waiter, ReportFilter.PaymentType], Receipt: true);

    public override async Task<ReportResult> Run(ReportContext c, CancellationToken ct)
    {
        var f = c.Filters;
        var ids = c.Period.PeriodIds;
        var payments = await (from p in c.Db.Payments.AsNoTracking()
                              join k in c.Db.Tickets.AsNoTracking() on p.TicketId equals k.Id
                              where ids.Contains(p.WorkPeriodId) && p.Status == PaymentStatus.Final
                              select new { p.Type, p.Amount, p.MpesaCode, p.TakenByUserId, p.BroughtByUserId, p.ShiftId, p.TakenAt, TicketId = k.Id, k.Number })
            .ToListAsync(ct);
        if (f.CashierId is { } cashierFilter) payments = payments.Where(p => p.TakenByUserId == cashierFilter).ToList();
        if (f.WaiterId is { } waiterFilter) payments = payments.Where(p => p.BroughtByUserId == waiterFilter).ToList();
        var typed = f.PaymentType is { } type ? payments.Where(p => p.Type == type).ToList() : payments;

        // Per cashier shift.
        var shifts = await c.Db.CashierShifts.AsNoTracking().Where(s => ids.Contains(s.WorkPeriodId))
            .Where(s => f.CashierId == null || s.CashierUserId == f.CashierId).OrderBy(s => s.OpenedAt).ToListAsync(ct);
        var shiftIds = shifts.Select(s => s.Id).ToList();
        var movements = await c.Db.DrawerMovements.AsNoTracking().Where(m => shiftIds.Contains(m.ShiftId))
            .Select(m => new { m.ShiftId, m.Kind, m.Amount }).ToListAsync(ct);
        decimal Taken(Guid shift, PaymentType t) => payments.Where(p => p.ShiftId == shift && p.Type == t).Sum(p => p.Amount);
        decimal Moved(Guid shift, DrawerMovementKind k) => movements.Where(m => m.ShiftId == shift && m.Kind == k).Sum(m => m.Amount);
        var shiftRows = shifts.Select(s =>
        {
            var cash = Taken(s.Id, PaymentType.Cash);
            var payOuts = Moved(s.Id, DrawerMovementKind.PayOut);
            var drops = Moved(s.Id, DrawerMovementKind.Drop);
            var expected = s.Expected ?? s.OpeningFloat + cash - payOuts - drops;
            return ReportRow.Of(
                $"{c.UserName(s.CashierUserId)} · {c.TerminalName(s.TerminalId)}", s.OpenedAt, s.OpeningFloat, cash,
                Taken(s.Id, PaymentType.Mpesa), Taken(s.Id, PaymentType.Card), payOuts, drops, expected, s.Counted, s.Difference,
                s.IsOpen ? "Open" : c.UserName(s.SignedOffByUserId));
        }).ToList();
        var shiftTable = new ReportTable("Cashier shifts",
        [
            new("shift", "Cashier · terminal"), new("opened", "Opened", ColumnType.DateTime), new("float", "Float", ColumnType.Money),
            new("cash", "Cash", ColumnType.Money), new("mpesa", "M-Pesa", ColumnType.Money), new("card", "Card", ColumnType.Money),
            new("payouts", "Pay-outs", ColumnType.Money), new("drops", "Drops", ColumnType.Money), new("expected", "Expected", ColumnType.Money),
            new("counted", "Counted", ColumnType.Money), new("difference", "Difference", ColumnType.Money), new("signed", "Signed off by"),
        ],
        [new ReportGroup(null, shiftRows)],
        ReportRow.Of("Total", null, shifts.Sum(s => s.OpeningFloat),
            shifts.Sum(s => Taken(s.Id, PaymentType.Cash)), shifts.Sum(s => Taken(s.Id, PaymentType.Mpesa)), shifts.Sum(s => Taken(s.Id, PaymentType.Card)),
            shifts.Sum(s => Moved(s.Id, DrawerMovementKind.PayOut)), shifts.Sum(s => Moved(s.Id, DrawerMovementKind.Drop)), null, null,
            shifts.Sum(s => s.Difference ?? 0), null));

        // Per waiter: what they brought in, and their bills still open.
        var openBills = await c.Db.Tickets.AsNoTracking()
            .Where(k => ids.Contains(k.WorkPeriodId) && k.Status == TicketStatus.Open && (f.WaiterId == null || k.OpenedByUserId == f.WaiterId))
            .Select(k => new
            {
                k.Id, k.Number, k.OpenedByUserId, k.OpenedAt, Table = k.Table != null ? k.Table.Name : k.CustomerName,
                Total = k.Lines.Where(l => l.Status != OrderLineStatus.Cancelled && l.Status != OrderLineStatus.Voided && l.VoidedAt == null)
                    .Sum(l => l.Gross - l.Discount),
            })
            .OrderBy(k => k.Number).ToListAsync(ct);
        var waiters = typed.Select(p => p.BroughtByUserId).Concat(openBills.Select(b => b.OpenedByUserId)).Distinct()
            .OrderBy(w => c.UserName(w)).ToList();
        decimal Brought(Guid w, PaymentType t) => typed.Where(p => p.BroughtByUserId == w && p.Type == t).Sum(p => p.Amount);
        var waiterTable = new ReportTable("Per waiter",
        [
            new("waiter", "Waiter"), new("cash", "Cash", ColumnType.Money), new("mpesa", "M-Pesa", ColumnType.Money),
            new("card", "Card", ColumnType.Money), new("total", "Brought in", ColumnType.Money), new("open", "Open bills", ColumnType.Integer),
            new("openTotal", "Open total", ColumnType.Money),
        ],
        [new ReportGroup(null, waiters.Select(w => ReportRow.Of(c.UserName(w), Brought(w, PaymentType.Cash), Brought(w, PaymentType.Mpesa),
            Brought(w, PaymentType.Card), typed.Where(p => p.BroughtByUserId == w).Sum(p => p.Amount),
            openBills.Count(b => b.OpenedByUserId == w), openBills.Where(b => b.OpenedByUserId == w).Sum(b => b.Total))).ToList())],
        ReportRow.Of("Total", typed.Where(p => p.Type == PaymentType.Cash).Sum(p => p.Amount), typed.Where(p => p.Type == PaymentType.Mpesa).Sum(p => p.Amount),
            typed.Where(p => p.Type == PaymentType.Card).Sum(p => p.Amount), typed.Sum(p => p.Amount), openBills.Count, openBills.Sum(b => b.Total)));

        var openTable = new ReportTable("Open bills",
            [new("ticket", "Ticket", ColumnType.Ticket), new("table", "Table"), new("waiter", "Waiter"), new("opened", "Opened", ColumnType.DateTime),
                new("total", "Total", ColumnType.Money)],
            [new ReportGroup(null, openBills.Select(b => new ReportRow([b.Number, b.Table ?? "", c.UserName(b.OpenedByUserId), b.OpenedAt, b.Total], b.Id)).ToList())]);

        var tables = new List<ReportTable> { shiftTable, waiterTable, openTable };
        if (f.PaymentType is null or PaymentType.Mpesa)
        {
            var mpesa = payments.Where(p => p.Type == PaymentType.Mpesa).OrderBy(p => p.TakenAt).ToList();
            var rows = mpesa.Select(p => new ReportRow([p.MpesaCode ?? "No code", p.Amount, p.Number, c.UserName(p.TakenByUserId),
                c.UserName(p.BroughtByUserId), p.TakenAt], p.TicketId)).ToList();
            var withCode = rows.Where(r => (string)r.Cells[0]! != "No code").ToList();
            var without = rows.Where(r => (string)r.Cells[0]! == "No code").ToList();
            var groups = new List<ReportGroup> { new("With a code", withCode, Total: mpesa.Where(p => p.MpesaCode != null).Sum(p => p.Amount)) };
            if (without.Count > 0) groups.Add(new ReportGroup("No code", without, Total: mpesa.Where(p => p.MpesaCode == null).Sum(p => p.Amount)));
            tables.Add(new ReportTable("M-Pesa codes",
                [new("code", "Code"), new("amount", "Amount", ColumnType.Money), new("ticket", "Ticket", ColumnType.Ticket), new("cashier", "Cashier"),
                    new("brought", "Brought by"), new("time", "Time", ColumnType.DateTime)],
                groups, ReportRow.Of("Total", mpesa.Sum(p => p.Amount), null, null, null, null)));
        }
        return await Result(c, tables, ct);
    }
}
