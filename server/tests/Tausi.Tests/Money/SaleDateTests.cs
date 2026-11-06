using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Day;
using Tausi.Application.Money;
using Tausi.Application.Reports;
using Tausi.Application.Settings;
using Tausi.Application.Settling;
using Tausi.Application.Support;
using Tausi.Domain.Ledger;
using Tausi.Domain.Money;
using Tausi.Domain.Service;
using Tausi.Tests.Stock;

namespace Tausi.Tests.Money;

/// <summary>
/// Version 1.0.1: everything counts on the calendar date it happened in the business's time zone, the
/// day changing at midnight, whichever work period is open. Items count on the date they were served
/// (printed), payments on the date they were taken. The fake clock starts on Tue 20 Oct 2026 at
/// 18:00 UTC, 21:00 in Nairobi; the server's own clock is UTC.
/// </summary>
public class SaleDateTests : StockTestBase
{
    private static readonly DateOnly Tuesday = new(2026, 10, 20);
    private static readonly TimeZoneInfo Nairobi = TimeZoneInfo.FindSystemTimeZoneById("Africa/Nairobi");

    private async Task InNairobi() => await F.SetSetting(SettingsRegistry.BusinessTimeZoneKey, "Africa/Nairobi");

    /// <summary>Moves the clock to this time in Nairobi on the date (never backwards); edna logs in again on Counter A.</summary>
    private async Task At(DateOnly date, int hour, int minute)
    {
        var target = new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(3));
        var now = F.Clock.GetUtcNow();
        if (target < now) throw new InvalidOperationException($"The clock is already past {target}.");
        F.Clock.Advance(target - now);
        await F.LoginOn("edna", "Counter A");
    }

    /// <summary>CAROL settles the ticket in cash for what's due, at the current time (a shift opened if needed).</summary>
    private async Task Settle(Guid ticket)
    {
        await F.LoginOn("CAROL", "Counter A");
        if ((await F.Get<Application.Cash.ShiftQueries>().Current(None)).Shift is null) await StartShift(1000);
        await TakeLock(ticket);
        var due = (await Detail(ticket)).AmountDue;
        await F.Get<PaymentCommands>().Add(ticket, new AddPaymentRequest(PaymentType.Cash, null, due), None);
        await F.Get<SettleTicket>().Execute(ticket, None);
        await F.LoginOn("edna", "Counter A");
    }

    /// <summary>The sales (gross after discounts) of settled tickets served on the date, from the shared sales query.</summary>
    private async Task<decimal> Sales(DateOnly date)
    {
        var period = await ReportContext.ResolvePeriod(F.Read(), new ReportFilters { From = date, To = date }, Nairobi, F.Clock.GetUtcNow(), None);
        return await SalesQuery.Lines(F.Read(), period, includeOpen: false).SumAsync(l => l.Gross - l.Discount, None);
    }

    /// <summary>What the books say was sold on the date: Sales and VAT credited by sale entries of that date.</summary>
    private decimal LedgerSales(DateOnly date)
    {
        var db = F.Read();
        return db.JournalLines.Include(l => l.Account)
            .Where(l => db.JournalEntries.Any(e => e.Id == l.JournalEntryId && e.BusinessDate == date && e.SourceType == SalePosting.SourceType))
            .AsEnumerable()
            .Where(l => l.Account!.Code is LedgerAccountCodes.Sales or LedgerAccountCodes.VatPayable)
            .Sum(l => l.Credit - l.Debit);
    }

    private decimal Balance(string code, DateOnly date) =>
        CashBook.Balances(F.Read(), date, None).GetAwaiter().GetResult().GetValueOrDefault(code);

    private List<JournalEntry> SaleEntries(Guid ticket) =>
        F.Read().JournalEntries.Include(e => e.Lines).ThenInclude(l => l.Account)
            .Where(e => e.SourceType == SalePosting.SourceType && e.SourceId == ticket.ToString()).ToList();

    [Fact]
    public async Task The_date_changes_at_midnight_in_the_business_time_zone_while_the_server_runs_in_UTC()
    {
        await InNairobi();
        await At(Tuesday, 23, 59);
        var late = await Printed("7", ("Tusker Lager", 1));
        await At(Tuesday.AddDays(1), 0, 1);
        var early = await Printed("8", ("Tusker Lager", 2));
        await Settle(late.Id);
        await Settle(early.Id);

        var one = (await Detail(late.Id)).AmountDue;
        (await Sales(Tuesday)).Should().Be(one, "23:59 is still Tuesday");
        (await Sales(Tuesday.AddDays(1))).Should().Be((await Detail(early.Id)).AmountDue, "00:01 is Wednesday");
        LedgerSales(Tuesday).Should().Be(await Sales(Tuesday));
        LedgerSales(Tuesday.AddDays(1)).Should().Be(await Sales(Tuesday.AddDays(1)));
    }

    [Fact]
    public async Task A_ticket_served_before_midnight_and_paid_after_puts_its_items_on_the_first_date_and_its_cash_on_the_second()
    {
        await InNairobi();
        await At(Tuesday, 23, 30);
        var ticket = await Printed("7", ("Tusker Lager", 2), ("Guiness", 1));
        var due = (await Detail(ticket.Id)).AmountDue;
        await At(Tuesday.AddDays(1), 0, 30);
        await Settle(ticket.Id);

        var entries = SaleEntries(ticket.Id);
        entries.Should().HaveCount(2).And.OnlyContain(e => e.Lines.Sum(l => l.Debit) == e.Lines.Sum(l => l.Credit));
        var served = entries.Single(e => e.BusinessDate == Tuesday);
        Dr(served, LedgerAccountCodes.UnsettledSales).Should().Be(due);
        (Cr(served, LedgerAccountCodes.Sales) + Cr(served, LedgerAccountCodes.VatPayable)).Should().Be(due);
        var paid = entries.Single(e => e.BusinessDate == Tuesday.AddDays(1));
        Dr(paid, LedgerAccountCodes.Cash).Should().Be(due);
        Cr(paid, LedgerAccountCodes.UnsettledSales).Should().Be(due);

        Balance(LedgerAccountCodes.UnsettledSales, Tuesday).Should().Be(due, "served on Tuesday, not yet paid at its end");
        Balance(LedgerAccountCodes.UnsettledSales, Tuesday.AddDays(1)).Should().Be(0);
        (await Sales(Tuesday)).Should().Be(due);
        (await Sales(Tuesday.AddDays(1))).Should().Be(0);
        LedgerSales(Tuesday).Should().Be(due);
        LedgerSales(Tuesday.AddDays(1)).Should().Be(0);
        (await F.Get<ConsistencyCheck>().Run(None)).Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task A_work_period_open_for_four_days_splits_its_sales_by_date_and_the_Z_report_says_so()
    {
        await InNairobi();
        var amounts = new List<decimal>();
        for (var day = 0; day < 4; day++)
        {
            await At(Tuesday.AddDays(day), 21, 30);
            var ticket = await Printed($"{day + 1}", ("Tusker Lager", day + 1));
            amounts.Add((await Detail(ticket.Id)).AmountDue);
            await Settle(ticket.Id);
        }

        for (var day = 0; day < 4; day++)
        {
            (await Sales(Tuesday.AddDays(day))).Should().Be(amounts[day]);
            LedgerSales(Tuesday.AddDays(day)).Should().Be(amounts[day], "the reports equal the ledger for every date");
        }
        var week = await ReportContext.ResolvePeriod(F.Read(), new ReportFilters { From = Tuesday, To = Tuesday.AddDays(3) }, Nairobi, F.Clock.GetUtcNow(), None);
        (await SalesQuery.Lines(F.Read(), week, includeOpen: false).SumAsync(l => l.Gross - l.Discount, None)).Should().Be(amounts.Sum(),
            "the daily totals add up to the period's");

        var period = F.Read().WorkPeriods.Single(p => p.IsOpen).Id;
        var z = await F.Get<DaySummaries>().For(period, None);
        z.Spans.Should().Be("This work period covered 4 days (Tue 20 – Fri 23 Oct).");
        z.Dates!.Select(d => d.Sales).Should().Equal(amounts);
        z.Dates!.Select(d => d.Taken).Should().Equal(amounts);
        z.Dates!.Sum(d => d.Sales).Should().Be(z.Gross - z.Discounts);
        z.Profit.Should().BeNull("the books' profit is per date, so a period of several dates shows it per date in the cash book");
    }

    [Fact]
    public async Task A_one_date_work_period_has_no_split_or_warning()
    {
        await InNairobi();
        var ticket = await Printed("7", ("Tusker Lager", 1));
        await Settle(ticket.Id);

        var z = await F.Get<DaySummaries>().For(F.Read().WorkPeriods.Single(p => p.IsOpen).Id, None);
        z.Spans.Should().BeNull();
        z.Dates.Should().BeEmpty();
        z.Profit.Should().NotBeNull();
    }

    [Fact]
    public async Task The_upgrade_moves_a_version_1_0_0_sale_to_its_calendar_dates_once()
    {
        await InNairobi();
        await At(Tuesday, 23, 30);
        var ticket = await Printed("7", ("Tusker Lager", 2));
        var due = (await Detail(ticket.Id)).AmountDue;
        await At(Tuesday.AddDays(1), 0, 30);
        await Settle(ticket.Id);

        // As version 1.0.0 posted it: one entry on the work period's date (Tuesday), and the migration's copy of that date.
        await using (var db = F.Read())
        {
            var mine = db.JournalEntries.Where(e => e.SourceId == ticket.Id.ToString());
            var paid = await mine.Where(e => e.BusinessDate == Tuesday.AddDays(1)).Select(e => e.Id).SingleAsync();
            var served = await mine.Where(e => e.BusinessDate == Tuesday).Select(e => e.Id).SingleAsync();
            var unsettled = await db.LedgerAccounts.Where(a => a.Code == LedgerAccountCodes.UnsettledSales).Select(a => a.Id).SingleAsync();
            await db.JournalLines.Where(l => l.AccountId == unsettled).ExecuteDeleteAsync();
            await db.JournalLines.Where(l => l.JournalEntryId == served).ExecuteUpdateAsync(u => u.SetProperty(l => l.JournalEntryId, paid));
            await db.JournalEntries.Where(e => e.Id == served).ExecuteDeleteAsync();
            await db.JournalEntries.Where(e => e.Id == paid).ExecuteUpdateAsync(u => u.SetProperty(e => e.BusinessDate, Tuesday));
            await db.JournalEntries.ExecuteUpdateAsync(u => u.SetProperty(e => e.OldBusinessDate, e => e.BusinessDate));
        }
        LedgerSales(Tuesday).Should().Be(due);
        Balance(LedgerAccountCodes.Cash, Tuesday).Should().Be(due, "1.0.0 put the cash on the work period's date");

        await F.Get<LedgerBackfill>().Run(None);

        var original = SaleEntries(ticket.Id).Single(e => e.Lines.Any(l => l.Account!.Code == LedgerAccountCodes.Cash));
        original.BusinessDate.Should().Be(Tuesday.AddDays(1), "paid after midnight");
        original.OldBusinessDate.Should().Be(Tuesday);
        Balance(LedgerAccountCodes.Cash, Tuesday).Should().Be(0, "the cash was taken on Wednesday");
        LedgerSales(Tuesday).Should().Be(due, "the items were served on Tuesday");
        LedgerSales(Tuesday.AddDays(1)).Should().Be(0);
        Balance(LedgerAccountCodes.UnsettledSales, Tuesday.AddDays(1)).Should().Be(0);
        (await Sales(Tuesday)).Should().Be(LedgerSales(Tuesday));
        AllEntriesBalance();
        (await F.Get<ConsistencyCheck>().Run(None)).Problems.Should().BeEmpty();

        var count = F.Read().JournalEntries.Count();
        await F.Get<LedgerBackfill>().Run(None);
        F.Read().JournalEntries.Count().Should().Be(count, "running it again changes nothing");
        LedgerSales(Tuesday).Should().Be(due);
    }
}
