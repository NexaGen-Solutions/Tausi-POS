using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Application.Cash;

public sealed record MySalesLineDto(
    Guid TicketId, long TicketNumber, DateTimeOffset At, string Name, string? Options, decimal Quantity, decimal Amount,
    bool Voided, bool Gift);

public sealed record MyOpenBillDto(Guid Id, long Number, string Where, decimal Total);

/// <summary>
/// My sales for the current work period, only my own figures: lines I added (after their discount
/// share; voids out, gifts at 0), the tickets they are on, the money I brought to the cashier by
/// type, and the bills still open on my name.
/// </summary>
public sealed record MySalesDto(
    string Name, DateTimeOffset? PeriodOpenedAt, decimal Total, int Tickets, decimal Cash, decimal Mpesa, decimal Card,
    IReadOnlyList<MyOpenBillDto> OpenBills, IReadOnlyList<MySalesLineDto> Lines);

public sealed class GetMySales(ITausiDb db, ICurrentSession session, IPermissionService permissions)
{
    public async Task<MySalesDto> Execute(CancellationToken ct)
    {
        var me = session.RequireUser();
        await permissions.Require(me, PermissionKeys.ReportsOwn, ct: ct);
        return await For(me.UserId, me.Name, ct);
    }

    /// <summary>Only ever this user's own lines and payments: the filters are on <paramref name="userId"/>.</summary>
    public async Task<MySalesDto> For(Guid userId, string name, CancellationToken ct)
    {
        var day = await WorkPeriods.Current(db, ct);
        var open = await db.Tickets.AsNoTracking().Include(k => k.Table).Include(k => k.Lines)
            .Where(k => k.Status == TicketStatus.Open && k.OpenedByUserId == userId).ToListAsync(ct);
        var bills = open.OrderBy(k => k.Number).Select(k => new MyOpenBillDto(k.Id, k.Number, TicketPrints.Where(k), k.Total)).ToList();
        if (day is null) return new MySalesDto(name, null, 0, 0, 0, 0, 0, bills, []);

        var lines = await db.OrderLines.AsNoTracking()
            .Where(l => l.AddedByUserId == userId && l.Status != OrderLineStatus.Cancelled
                && db.Tickets.Any(k => k.Id == l.TicketId && k.WorkPeriodId == day.Id))
            .Select(l => new
            {
                l.TicketId,
                Number = db.Tickets.Where(k => k.Id == l.TicketId).Select(k => k.Number).First(),
                l.AddedAt, l.Name, l.Options, l.Quantity, l.Gross, l.Discount, l.Status, l.VoidedAt, l.IsGift,
            })
            .ToListAsync(ct);
        var shown = lines
            .Select(l =>
            {
                var voided = l.Status == OrderLineStatus.Voided || l.VoidedAt != null;
                return new MySalesLineDto(l.TicketId, l.Number, l.AddedAt, l.Name, l.Options, l.Quantity,
                    voided ? l.Gross : l.Gross - l.Discount, voided, l.IsGift);
            })
            .OrderByDescending(l => l.At).ThenByDescending(l => l.TicketNumber).ToList();
        var counted = shown.Where(l => !l.Voided).ToList();

        var brought = await db.Payments.AsNoTracking()
            .Where(p => p.BroughtByUserId == userId && p.WorkPeriodId == day.Id && p.Status != PaymentStatus.Removed)
            .Select(p => new { p.Type, p.Amount }).ToListAsync(ct);
        return new MySalesDto(name, day.OpenedAt, counted.Sum(l => l.Amount), shown.Select(l => l.TicketId).Distinct().Count(),
            brought.Where(p => p.Type == PaymentType.Cash).Sum(p => p.Amount),
            brought.Where(p => p.Type == PaymentType.Mpesa).Sum(p => p.Amount),
            brought.Where(p => p.Type == PaymentType.Card).Sum(p => p.Amount),
            bills, shown);
    }
}

/// <summary>The waiter's own slip on this terminal's receipt printer (needs <c>reports.own</c>).</summary>
public sealed class PrintMySales(ITausiDb db, ICurrentSession session, IPermissionService permissions, GetMySales mySales, CashPrints prints)
{
    public async Task Execute(CancellationToken ct)
    {
        var me = session.RequireUser();
        await permissions.Require(me, PermissionKeys.ReportsOwn, ct: ct);
        if (me.TerminalId is not { } terminalId) throw new ForbiddenException("Slips print on a terminal.", "terminal_required");
        var sales = await mySales.For(me.UserId, me.Name, ct);
        var job = await prints.Print(terminalId, (style, business, local) => CashSlips.Waiter(sales, business, local, style), ct)
            ?? throw new Domain.Common.DomainException("This terminal has no receipt printer. Set one in the admin site under Terminals and printers.");
        await db.SaveChangesAsync(ct);
        _ = job;
    }
}
