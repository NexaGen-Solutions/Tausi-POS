using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Cash;
using Tausi.Application.Day;
using Tausi.Application.Money;
using Tausi.Application.Printing;
using Tausi.Application.Reports;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Settling;
using Tausi.Application.Stock;
using Tausi.Application.Tickets;
using Tausi.Domain.Ledger;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;
using Tausi.Domain.Stock;
using Tausi.Infrastructure.Platform;
using Tausi.Tests.Stock;

namespace Tausi.Tests.Reports;

/// <summary>
/// The reports on a busy day: discounts, gifts, voids (poured, not poured, partial), a price change,
/// rounding, a merged ticket, an open ticket, a reversed delivery and a count. The fake clock is
/// 20 Oct 2026 18:00 UTC (21:00 in Nairobi).
/// </summary>
public class ReportsTests : StockTestBase
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private ReportService Reports => F.Get<ReportService>();

    private Task<ReportResult> Run(string key, ReportFilters? filters = null) => Reports.Run(key, filters ?? new ReportFilters(), None);

    private static readonly ReportFilters Settled = new() { IncludeOpen = false };

    private sealed record Day(Guid Edna1, Guid Ruth2, Guid Merged3, Guid Merged4, Guid Open5, Guid Reversed, Guid Count);

    /// <summary>A busy day; at the end the Administrator is logged in.</summary>
    private async Task<Day> BusyDay()
    {
        await F.SetSetting(SettingsRegistry.BusinessTimeZoneKey, "Africa/Nairobi");

        // edna's table 7: a poured void, a gift, a price change and a 10% discount (which makes rounding).
        var t1 = await Printed("7", ("Tusker Lager", 3), ("Guiness", 2), ("Pool Table", 1));
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(t1.Id);
        await Void(t1.Id, LineOf(t1, "Guiness"), 1, poured: true);
        await Gift(t1.Id, LineOf(t1, "Tusker Lager"));
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.OrderChangePrice, "Administrator");
        await ChangePrice(t1.Id, LineOf(t1, "Pool Table"), 35);
        F.Approval.ApprovalToken = null;
        await Discount(t1.Id, DiscountKind.Percent, 10);

        // Ruth's table 8, with a line edna added: credited differently by "who opened the ticket".
        await F.LoginOn("Ruth", "Counter B");
        var t2 = await F.NewTicket("8", ("Heineken", 2));
        await F.Get<PrintTicket>().Print(t2.Id, None);
        await F.LoginOn("edna", "Counter A");
        await TakeLock(t2.Id);
        await F.Add(t2.Id, "Tusker Lager", options: ["Cold"]);
        await F.Get<PrintTicket>().Print(t2.Id, None);

        // Two tickets on table 9 merged; then a partial void that wasn't poured.
        var t3 = await Printed("9", ("Tusker Lager", 2));
        var t4 = await Printed("9", ("Sodas 300ml", 1));
        await F.Get<MergeTickets>().Execute(new MergeTicketsRequest([t4.Id], t3.Id), None);
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(t3.Id);
        await Void(t3.Id, LineOf(await Detail(t3.Id), "Tusker Lager"), 1, poured: false);

        // An open ticket.
        await F.LoginOn("edna", "Counter A");
        var t5 = await Printed("10", ("Tusker Lager", 1));

        // CAROL settles 1 (cash and M-Pesa), 2 (cash) and 3 (card).
        await F.LoginOn("CAROL", "Counter A");
        await StartShift(1000);
        Later();
        foreach (var (id, how) in new[] { (t1.Id, "split"), (t2.Id, "cash"), (t3.Id, "card") })
        {
            await TakeLock(id);
            var due = (await Detail(id)).AmountDue;
            var pay = F.Get<PaymentCommands>();
            if (how == "split")
            {
                await pay.Add(id, new AddPaymentRequest(PaymentType.Cash, null, 100), None);
                await pay.Add(id, new AddPaymentRequest(PaymentType.Mpesa, due - 100, null, "SJK3ABC777"), None);
            }
            else if (how == "cash") await pay.Add(id, new AddPaymentRequest(PaymentType.Cash, null, due), None);
            else await pay.Add(id, new AddPaymentRequest(PaymentType.Card, due, null, null, "4242"), None);
            await F.Get<SettleTicket>().Execute(id, None);
        }

        // Stock: a delivery reversed, one kept on credit, and a count with a difference.
        await AsAdmin();
        var reversed = await Deliver("D1");
        await F.Get<DeliveryCommands>().Reverse(reversed.Id, new ReverseDeliveryRequest("Wrong invoice"), None);
        await Deliver("D2");
        await F.LoginOn("SAM", "Counter B");
        var counts = F.Get<StockCountCommands>();
        var count = await counts.Start(new StartCountRequest(Location("Bar"), StockCountKind.Full, false, null), None);
        var entries = count.Lines.Select(l => new CountEntry(l.StockItemId,
            new() { [l.BaseUnit] = OnHand(l.StockItem, "Bar") - (l.StockItem == "Guiness" ? 2 : 0) }, null)).ToList();
        await counts.Enter(count.Id, new EnterCountsRequest(entries), None);
        await counts.Submit(count.Id, None);
        await AsAdmin();
        await counts.Approve(count.Id, None);
        return new Day(t1.Id, t2.Id, t3.Id, t4.Id, t5.Id, reversed.Id, count.Id);
    }

    private static decimal GrandTotal(ReportResult r, int table = 0, int column = -1)
    {
        var t = r.Tables[table];
        var i = column >= 0 ? column : t.Columns.ToList().FindLastIndex(c => c.Type == ColumnType.Money);
        return (decimal)t.Totals!.Cells[i]!;
    }

    private static int Column(ReportTable t, string key) => t.Columns.ToList().FindIndex(c => c.Key == key);

    /// <summary>Credit − debit on an account over every entry (or those <paramref name="which"/> picks).</summary>
    private decimal LedgerNet(string code, Func<JournalEntry, bool>? which = null) =>
        LedgerLines(code, which).Sum(l => l.Line.Credit - l.Line.Debit);

    private List<(JournalLine Line, JournalEntry Entry)> LedgerLines(string code, Func<JournalEntry, bool>? which = null)
    {
        var entries = F.Read().JournalEntries.Include(e => e.Lines).ThenInclude(l => l.Account).ToList();
        return entries.Where(e => which is null || which(e))
            .SelectMany(e => e.Lines.Where(l => l.Account!.Code == code).Select(l => (l, e))).ToList();
    }

    // ---- Agreement ----

    [Fact]
    public async Task Every_sales_figure_agrees_with_the_others_and_with_the_ledger()
    {
        await BusyDay();
        var sales = LedgerNet(LedgerAccountCodes.Sales) + LedgerNet(LedgerAccountCodes.VatPayable, e => e.SourceType == SalePosting.SourceType);
        sales.Should().BeGreaterThan(0);

        var byUser = await Run("sales-by-user", Settled);
        var byOpener = await Run("sales-by-user", Settled with { CreditTo = CreditTo.OpenedBy });
        var byItem = await Run("sales-by-item", Settled);
        var daily = (await Summaries.For(F.Read(), Today, includeLate: true, None)).SalesInclVat;
        var tickets = await Run("tickets", Settled);
        var vat = await Run("vat");

        GrandTotal(byUser).Should().Be(sales);
        GrandTotal(byOpener).Should().Be(sales, "who gets the credit changes, the total doesn't");
        var itemTable = byItem.Tables[0];
        ((decimal)itemTable.Totals!.Cells[Column(itemTable, "gross")]! - (decimal)itemTable.Totals.Cells[Column(itemTable, "discount")]!).Should().Be(sales);
        daily.Should().Be(sales);
        GrandTotal(tickets, column: Column(tickets.Tables[0], "total")).Should().Be(sales);
        GrandTotal(vat, table: 1, column: 1).Should().Be(sales);

        byUser.Tables[0].Groups.Select(g => g.Title).Should().Contain(["edna", "Ruth"]);
        byUser.Tables[0].Groups.Single(g => g.Title == "edna").Total.Should()
            .NotBe(byOpener.Tables[0].Groups.Single(g => g.Title == "edna").Total, "edna added a Tusker to Ruth's ticket");

        // The Z report adds up the same way.
        var period = F.Read().WorkPeriods.Single().Id;
        var z = await F.Get<DaySummaries>().For(period, None);
        (z.Gross - z.Discounts).Should().Be(sales);
        z.Rounding.Should().Be(LedgerNet(LedgerAccountCodes.Rounding)).And.NotBe(0, "the 10% discount leaves cents to round");
    }

    [Fact]
    public async Task Open_tickets_count_by_default_for_the_open_day_with_a_note()
    {
        var day = await BusyDay();
        var open = (await Detail(day.Open5)).Total;

        var withOpen = await Run("sales-by-user");
        var settled = await Run("sales-by-user", Settled);

        GrandTotal(withOpen).Should().Be(GrandTotal(settled) + open);
        withOpen.Notes.Should().Contain("Includes 1 open ticket");
        withOpen.FilterText.Should().Contain("Open tickets included");
        settled.Notes.Should().NotContain(n => n.StartsWith("Includes"));
    }

    [Fact]
    public async Task The_vat_summary_equals_the_vat_payable_per_rate_and_per_day()
    {
        await BusyDay();
        var vat = await Run("vat");

        var ledger = LedgerLines(LedgerAccountCodes.VatPayable, e => e.SourceType == SalePosting.SourceType)
            .GroupBy(l => l.Line.Memo).ToDictionary(g => g.Key, g => g.Sum(l => l.Line.Credit - l.Line.Debit));
        var totals = vat.Tables[1].Groups[0].Rows;
        foreach (var row in totals)
        {
            var label = (string)row.Cells[0]!;
            var memo = label == "Exempt" ? "VAT exempt" : label == "Zero-rated" ? "VAT 0%" : label;
            ((decimal)row.Cells[3]!).Should().Be(ledger.GetValueOrDefault(memo), $"VAT at {label}");
        }
        vat.Tables[0].Groups.Should().ContainSingle().Which.Title.Should().Be("Tue 20 Oct 2026");
        ((decimal)vat.Tables[0].Groups[0].Subtotal!.Cells[3]!).Should().Be(ledger.Values.Sum());
    }

    [Fact]
    public async Task Payments_equal_the_cash_mpesa_and_card_debits_from_settling()
    {
        await BusyDay();
        var payments = await Run("payments");
        var waiters = payments.Tables.Single(t => t.Title == "Per waiter");
        decimal Debits(string code) => -LedgerNet(code, e => e.SourceType == SalePosting.SourceType);

        ((decimal)waiters.Totals!.Cells[1]!).Should().Be(Debits(LedgerAccountCodes.Cash));
        ((decimal)waiters.Totals.Cells[2]!).Should().Be(Debits(LedgerAccountCodes.MPesa));
        ((decimal)waiters.Totals.Cells[3]!).Should().Be(Debits(LedgerAccountCodes.CardClearing));
        ((int)waiters.Totals.Cells[5]!).Should().Be(1, "one open bill");

        var codes = payments.Tables.Single(t => t.Title == "M-Pesa codes");
        codes.Groups[0].Rows.Should().ContainSingle(r => (string)r.Cells[0]! == "SJK3ABC777" && r.TicketId != null);
        var shift = payments.Tables.Single(t => t.Title == "Cashier shifts").Groups[0].Rows.Single();
        ((string)shift.Cells[0]!).Should().StartWith("CAROL");
        ((decimal)shift.Cells[2]!).Should().Be(1000);
    }

    [Fact]
    public async Task Stock_on_hand_as_at_a_date_is_the_sum_of_movements_and_today_equals_the_cache()
    {
        await BusyDay();
        var today = await Run("stock-on-hand");
        var rows = today.Tables[0].Groups.SelectMany(g => g.Rows).ToList();
        var levels = F.Read().StockLevels.ToList();
        var items = F.Read().StockItems.ToDictionary(i => i.Name, i => i.Id);
        var locations = F.Read().StockLocations.ToDictionary(l => l.Name, l => l.Id);
        foreach (var row in rows)
        {
            var level = levels.Single(l => l.StockItemId == items[(string)row.Cells[0]!] && l.LocationId == locations[(string)row.Cells[1]!]);
            ((decimal)row.Cells[3]!).Should().Be(level.OnHand, $"{row.Cells[0]} at {row.Cells[1]}");
        }
        CacheMatchesMovements();

        var before = await Run("stock-on-hand", new ReportFilters { AsAt = Today.AddDays(-1) });
        var end = ReportContext.Midnight(Today, TimeZoneInfo.FindSystemTimeZoneById("Africa/Nairobi"));
        var movements = F.Read().StockMovements.AsEnumerable().Where(m => m.At < end)
            .GroupBy(m => (m.StockItemId, m.LocationId)).ToDictionary(g => g.Key, g => g.Sum(m => m.Quantity));
        foreach (var row in before.Tables[0].Groups.SelectMany(g => g.Rows))
            ((decimal)row.Cells[3]!).Should().Be(movements[(items[(string)row.Cells[0]!], locations[(string)row.Cells[1]!])]);
        before.FilterText.Should().Contain("As at the end of Mon 19 Oct 2026");
    }

    [Fact]
    public async Task Stock_variance_equals_the_counts_journal_entry_and_shows_a_trend()
    {
        var day = await BusyDay();
        var variance = await Run("stock-variance");
        var entry = Entries(StockCountCommands.LedgerSource).Single(e => e.SourceId == day.Count.ToString());

        GrandTotal(variance).Should().Be(Cr(entry, LedgerAccountCodes.StockLoss) - Dr(entry, LedgerAccountCodes.StockLoss));
        GrandTotal(variance).Should().BeLessThan(0, "two Guiness short");
        variance.Tables[0].Footer.Should().Contain(f => f.StartsWith("Items short: 1"));
        variance.Notes.Single().Should().Contain("counted by SAM").And.Contain("approved by Administrator");

        var trend = await Run("stock-variance", new ReportFilters { StockItemId = Item("Guiness") });
        trend.Tables[0].Groups[0].Rows.Should().ContainSingle().Which.Cells[4].Should().Be(-2m);
    }

    [Fact]
    public async Task Purchases_end_with_what_the_ledger_says_suppliers_are_owed()
    {
        await BusyDay();
        var purchases = await Run("purchases");
        var payables = LedgerNet(LedgerAccountCodes.SupplierPayables);

        var kbl = purchases.Tables[0].Groups.Single(g => g.Title == "KBL");
        kbl.Rows.Should().HaveCount(2);
        kbl.Rows.Should().ContainSingle(r => (string?)r.Cells[5] == "Reversed");
        kbl.Footer!.Single().Should().EndWith($"= owed at the end KSh {ReportFormat.Money(payables)}");
        purchases.Tables[0].Footer!.Single().Should().Be($"Owed to suppliers at the end: KSh {ReportFormat.Money(payables)}");
    }

    [Fact]
    public async Task My_sales_for_each_user_equals_their_group_in_sales_by_user()
    {
        await BusyDay();
        var byUser = await Run("sales-by-user");
        foreach (var name in new[] { "edna", "Ruth" })
        {
            var user = F.User(name);
            var mine = await F.Get<GetMySales>().For(user.Id, name, None);
            mine.Total.Should().Be(byUser.Tables[0].Groups.Single(g => g.Title == name).Total, name);
        }
    }

    // ---- The reports' contents ----

    [Fact]
    public async Task Sales_by_item_shows_cost_profit_and_margin_and_no_recipe()
    {
        await BusyDay();
        var result = await Run("sales-by-item", Settled);
        var rows = result.Tables[0].Groups.SelectMany(g => g.Rows).ToList();
        var tusker = rows.Single(r => (string)r.Cells[0]! == "Tusker Lager");
        ((decimal)tusker.Cells[6]!).Should().BeGreaterThan(0);
        ((decimal)tusker.Cells[7]!).Should().Be((decimal)tusker.Cells[4]! - (decimal)tusker.Cells[6]!);
        tusker.Cells[8].Should().NotBeNull();
        ((decimal)tusker.Cells[9]!).Should().Be(1, "one gifted");
        ((decimal)tusker.Cells[10]!).Should().Be(1, "one voided on the merged ticket");
        rows.Single(r => (string)r.Cells[0]! == "Pool Table").Cells[6].Should().Be("No recipe");
        result.Notes.Should().Contain(n => n.StartsWith("1 item has no recipe"));
    }

    [Fact]
    public async Task Voids_gifts_and_approvals_show_who_asked_who_approved_poured_and_totals_per_approver()
    {
        await BusyDay();
        var result = await Run("voids");
        var events = result.Tables[0].Groups[0].Rows;
        var poured = events.Single(r => (string)r.Cells[1]! == "Void" && (string?)r.Cells[10] == "Poured");
        poured.Cells[7].Should().Be("SAM");
        events.Should().Contain(r => (string)r.Cells[1]! == "Void" && (string?)r.Cells[10] == "Not poured");
        events.Single(r => (string)r.Cells[1]! == "Price change").Cells[8].Should().Be("Administrator");
        events.Should().Contain(r => (string)r.Cells[1]! == "Merge");
        result.Tables.Single(t => t.Title == "Totals by kind").Groups[0].Rows.Select(r => r.Cells[0]).Should()
            .Contain(["Void", "Gift", "Discount", "Price change", "Merge"]);
        result.Tables.Single(t => t.Title == "Totals by approver").Groups[0].Rows.Select(r => r.Cells[0]).Should().Contain(["SAM", "Administrator"]);

        var onlyAdmin = await Run("voids", new ReportFilters { ApproverId = F.User("Administrator").Id });
        onlyAdmin.Tables[0].Groups[0].Rows.Should().OnlyContain(r => (string?)r.Cells[8] == "Administrator");
    }

    [Fact]
    public async Task Price_changes_list_menu_and_ticket_changes()
    {
        await BusyDay();
        var result = await Run("price-changes");
        var onTickets = result.Tables.Single(t => t.Title == "On-ticket price changes");
        var row = onTickets.Groups[0].Rows.Single();
        row.Cells[1].Should().Be("Pool Table");
        ((decimal)row.Cells[5]!).Should().Be((35 - (decimal)row.Cells[2]!) * 1);
        row.TicketId.Should().NotBeNull();
        (await Run("price-changes", new ReportFilters { Kind = "Menu" })).Tables.Should().ContainSingle(t => t.Title == "Menu price changes");
    }

    [Fact]
    public async Task Tickets_and_receipts_filter_by_status_flags_and_amount()
    {
        var day = await BusyDay();
        var all = await Run("tickets");
        all.Tables[0].Groups[0].Rows.Select(r => r.TicketId).Should().Contain([day.Edna1, day.Ruth2, day.Merged3, day.Merged4, day.Open5]);
        all.Tables[0].Groups[0].Rows.Single(r => r.TicketId == day.Merged4).Cells[9].Should().Be("Merged");

        (await Run("tickets", new ReportFilters { Status = TicketStatus.Open })).Tables[0].Groups[0].Rows.Should()
            .ContainSingle().Which.TicketId.Should().Be(day.Open5);
        (await Run("tickets", new ReportFilters { HasVoids = true })).Tables[0].Groups[0].Rows.Select(r => r.TicketId).Should()
            .BeEquivalentTo([day.Edna1, day.Merged3]);
        (await Run("tickets", new ReportFilters { HasDiscount = true })).Tables[0].Groups[0].Rows.Should().ContainSingle();
        var big = await Run("tickets", new ReportFilters { MinAmount = 400 });
        big.Tables[0].Groups[0].Rows.Should().OnlyContain(r => (decimal)r.Cells[6]! >= 400);
        big.FilterText.Should().Contain("Amount: 400 to any");
    }

    [Fact]
    public async Task Users_roles_and_terminals_count_logins_tickets_and_approvals()
    {
        await BusyDay();
        var result = await Run("users");
        var rows = result.Tables[0].Groups.SelectMany(g => g.Rows).ToList();
        var edna = rows.Single(r => (string)r.Cells[0]! == "edna");
        ((string)edna.Cells[2]!).Should().Contain("Counter A");
        ((int)edna.Cells[5]!).Should().Be(4);
        var sam = rows.Single(r => (string)r.Cells[0]! == "SAM");
        ((int)sam.Cells[8]!).Should().Be(0, "SAM did the voids, gift and discount on their own say-so");
        ((int)rows.Single(r => (string)r.Cells[0]! == "Administrator").Cells[8]!).Should().Be(1, "the price change");

        var waiters = await Run("users", new ReportFilters { RoleId = F.Roles["Waiter"].Id });
        waiters.Tables[0].Groups.Should().ContainSingle().Which.Title.Should().Be("Waiter");
    }

    [Fact]
    public async Task The_daily_summary_has_a_row_per_date_and_reads_down_for_one_day()
    {
        await BusyDay();
        var one = await Run("daily-summary");
        one.Tables[0].Columns.Should().HaveCount(2);
        one.Tables[0].Groups[0].Rows.Select(r => r.Cells[0]).Should().Contain(["Sales (incl. VAT)", "Rounding", "Net for the day"]);

        var range = await Run("daily-summary", new ReportFilters { From = Today.AddDays(-2), To = Today });
        range.Tables[0].Groups[0].Rows.Should().HaveCount(3);
        var summary = await Summaries.For(F.Read(), Today, includeLate: true, None);
        GrandTotal(range).Should().Be(summary.Net);
        ((decimal)range.Tables[0].Totals!.Cells[1]!).Should().Be(summary.SalesInclVat);
    }

    [Fact]
    public async Task The_audit_log_searches_its_entries()
    {
        await BusyDay();
        var result = await Run("audit", new ReportFilters { Action = "order." });
        result.Tables[0].Groups[0].Rows.Should().NotBeEmpty().And.OnlyContain(r => ((string)r.Cells[3]!).StartsWith("order."));
        var search = await Run("audit", new ReportFilters { Search = "Wrong invoice" });
        search.Tables[0].Groups[0].Rows.Should().ContainSingle().Which.Cells[3].Should().Be("stock.delivery_reversed");
    }

    // ---- Permissions and filters ----

    [Fact]
    public async Task Reports_need_reports_all_and_the_audit_log_permissions_manage()
    {
        await F.LoginOn("edna", "Counter A");
        (await Reports.List(None)).Should().BeEmpty("a waiter only has reports.own");
        await FluentActions.Awaiting(() => Run("sales-by-user")).Should().ThrowAsync<PermissionDeniedException>();
        await FluentActions.Awaiting(() => F.Get<TicketDetailReport>().Execute(Guid.NewGuid(), None)).Should().ThrowAsync<PermissionDeniedException>();

        await F.LoginOn("SAM", "Counter A");
        var list = (await Reports.List(None)).Select(r => r.Key).ToList();
        list.Should().Contain("sales-by-user").And.NotContain("audit");
        await FluentActions.Awaiting(() => Run("audit")).Should().ThrowAsync<PermissionDeniedException>();

        await AsAdmin();
        (await Reports.List(None)).Select(r => r.Key).Should().Equal(ReportService.Order);
    }

    [Fact]
    public async Task Filters_narrow_each_sales_report()
    {
        await BusyDay();
        var ruth = F.User("Ruth").Id;
        var onlyRuth = await Run("sales-by-user", Settled with { UserId = ruth });
        onlyRuth.Tables[0].Groups.Should().ContainSingle().Which.Title.Should().Be("Ruth");
        onlyRuth.FilterText.Should().Contain("User: Ruth");

        var counterB = await Run("sales-by-user", Settled with { TerminalId = F.Read().Terminals.Single(t => t.Name == "Counter B").Id });
        counterB.Tables[0].Groups.SelectMany(g => g.Rows).Should().OnlyContain(r => ((string)r.Cells[2]!).StartsWith("Heineken"));

        var beers = F.Read().Categories.Single(c => c.Name == "Beers").Id;
        var items = await Run("sales-by-item", Settled with { CategoryId = beers });
        items.Tables[0].Groups.Should().ContainSingle().Which.Title.Should().Be("Beers");

        var cash = await Run("payments", new ReportFilters { PaymentType = PaymentType.Card });
        cash.Tables.Should().NotContain(t => t.Title == "M-Pesa codes");
    }

    [Fact]
    public async Task Filters_narrow_the_stock_money_and_audit_reports()
    {
        var day = await BusyDay();
        Rows(await Run("stock-on-hand", new ReportFilters { LocationId = Location("Store") })).Should().OnlyContain(r => (string)r.Cells[1]! == "Store");
        (await Run("stock-on-hand", new ReportFilters { StockCategory = StockCategory.Beer })).Tables[0].Groups.Should()
            .ContainSingle().Which.Title.Should().Be("Beer");
        Rows(await Run("stock-on-hand", new ReportFilters { OnlyBelowReorder = true })).Should().OnlyContain(r => r.Cells[8] != null);

        (await Run("purchases", new ReportFilters { SupplierId = Supplier() })).Tables[0].Groups.Should().ContainSingle().Which.Title.Should().Be("KBL");
        Rows(await Run("purchases", new ReportFilters { LocationId = Location("Bar") })).Should().BeEmpty("both deliveries went to the Store");

        var payments = await Run("payments", new ReportFilters { WaiterId = F.User("Ruth").Id });
        payments.Tables.Single(t => t.Title == "Per waiter").Groups[0].Rows.Should().ContainSingle().Which.Cells[0].Should().Be("Ruth");
        (await Run("payments", new ReportFilters { CashierId = F.User("SAM").Id })).Tables.Single(t => t.Title == "Cashier shifts").Groups[0].Rows
            .Should().BeEmpty();

        Rows(await Run("voids", new ReportFilters { Kind = "Gift" })).Should().OnlyContain(r => (string)r.Cells[1]! == "Gift").And.ContainSingle();
        Rows(await Run("voids", new ReportFilters { RequesterId = F.User("edna").Id })).Should().OnlyContain(r => (string)r.Cells[7]! == "edna");

        Rows(await Run("tickets", new ReportFilters { TableId = F.Table("8").Id })).Should().ContainSingle().Which.TicketId.Should().Be(day.Ruth2);
        Rows(await Run("audit", new ReportFilters { Entity = "Ticket" })).Should().OnlyContain(r => ((string)r.Cells[4]!).StartsWith("Ticket "));
        Rows(await Run("audit", new ReportFilters { UserId = F.User("CAROL").Id })).Should().OnlyContain(r => (string)r.Cells[1]! == "CAROL");
    }

    private static List<ReportRow> Rows(ReportResult result, int table = 0) => result.Tables[table].Groups.SelectMany(g => g.Rows).ToList();

    [Fact]
    public async Task A_work_period_past_midnight_stays_on_its_date_in_a_date_range()
    {
        await F.SetSetting(SettingsRegistry.BusinessTimeZoneKey, "Africa/Nairobi");
        F.Clock.Advance(TimeSpan.FromHours(4)); // 01:00 on the 21st in Nairobi; the day opened on the 20th
        await F.LoginOn("edna", "Counter A");
        var ticket = await Printed("7", ("Tusker Lager", 1));
        await AsAdmin();

        var friday = await Run("tickets", new ReportFilters { From = Today, To = Today });
        friday.Tables[0].Groups[0].Rows.Should().ContainSingle().Which.TicketId.Should().Be(ticket.Id);
        (await Run("tickets", new ReportFilters { From = Today.AddDays(1), To = Today.AddDays(1) })).IsEmpty.Should().BeTrue();
        friday.Period.Should().Be("Tue 20 Oct 2026");
    }

    [Theory]
    [InlineData(QuickRange.Today, "2026-10-21", "2026-10-21")]
    [InlineData(QuickRange.Yesterday, "2026-10-20", "2026-10-20")]
    [InlineData(QuickRange.ThisWeek, "2026-10-19", "2026-10-21")]
    [InlineData(QuickRange.LastWeek, "2026-10-12", "2026-10-18")]
    [InlineData(QuickRange.ThisMonth, "2026-10-01", "2026-10-21")]
    [InlineData(QuickRange.LastMonth, "2026-09-01", "2026-09-30")]
    public void Quick_picks_are_calendar_ranges(QuickRange quick, string from, string to)
    {
        ReportContext.Range(quick, new DateOnly(2026, 10, 21)).Should().Be((DateOnly.Parse(from), DateOnly.Parse(to)));
    }

    // ---- Exports and printing ----

    [Fact]
    public async Task Excel_has_numbers_bold_group_and_subtotal_rows_and_csv_is_flat()
    {
        await BusyDay();
        var file = await Reports.Export("sales-by-item", Settled, "xlsx", None);
        file.FileName.Should().EndWith(".xlsx");
        using var book = new XLWorkbook(new MemoryStream(file.Content));
        var sheet = book.Worksheet(1);
        sheet.Cell(1, 1).GetString().Should().Be("Sales by item");
        var header = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Item");
        header.Cell(1).Style.Font.Bold.Should().BeTrue();
        sheet.SheetView.SplitRow.Should().Be(header.RowNumber(), "the header row is frozen");
        var group = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Beers");
        group.Cell(1).Style.Font.Bold.Should().BeTrue();
        var tusker = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Tusker Lager");
        tusker.Cell(3).DataType.Should().Be(XLDataType.Number);
        tusker.Cell(3).Style.NumberFormat.Format.Should().Contain("KSh");
        tusker.Cell(1).Style.Font.Bold.Should().BeFalse();
        sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Beers total").Cell(3).Style.Font.Bold.Should().BeTrue();

        var csv = Encoding.UTF8.GetString((await Reports.Export("sales-by-item", Settled, "csv", None)).Content).TrimStart('﻿');
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines[0].Should().Be("Group,Item,Qty,Gross,Discount,Net of VAT,VAT,Cost of sales,Gross profit,Margin,Gifts,Voids");
        lines.Should().Contain(l => l.StartsWith("Beers,Tusker Lager,"));
        lines.Should().NotContain(l => l.Contains("total"), "no subtotal or total rows in the CSV");
    }

    [Fact]
    public void The_pdf_wraps_a_long_name_repeats_headers_and_numbers_its_pages()
    {
        var name = string.Concat(Enumerable.Repeat("Extraordinarily long cocktail name ", 4))[..120];
        var rows = Enumerable.Range(1, 120).Select(i => ReportRow.Of((long)(1000 + i), DateTimeOffset.UnixEpoch, i == 1 ? name : $"Item {i}", 1m, 250m)).ToList();
        var result = new ReportResult("sales-by-user", "Sales by user", "Tue 20 Oct 2026", [], DateTimeOffset.UnixEpoch,
            [new ReportTable(null, SalesByUserReport.Columns, [new ReportGroup("edna", rows, Subtitle: "Waiter", Total: 30000m)])], []);

        var pdf = Encoding.Latin1.GetString(ReportPdf.Build(result, "Test bar", TimeZoneInfo.Utc));
        var pages = pdf.Split("/Type /Page ").Length - 1;
        pages.Should().BeGreaterThan(1);
        pdf.Should().Contain($"(Page 1 of {pages})").And.Contain($"(Page {pages} of {pages})");
        CountOf(pdf, "(TICKET)").Should().Be(pages, "the column headers repeat on every page");
        var shown = string.Concat(System.Text.RegularExpressions.Regex.Matches(pdf, @"\((Extraord[^)]*|[^()]*name[^)]*)\) Tj").Select(m => m.Groups[1].Value + " "));
        shown.Replace(" ", "").Should().Contain(name.Replace(" ", ""), "every character of the name is printed");
        ReportPdf.Wrap(name, 150, 8.5f, false).Should().HaveCountGreaterThan(1);
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    [Theory]
    [InlineData("sales-by-user", 42)]
    [InlineData("sales-by-user", 32)]
    [InlineData("payments", 42)]
    [InlineData("payments", 32)]
    [InlineData("daily-summary", 42)]
    [InlineData("daily-summary", 32)]
    public async Task Receipt_versions_match_their_golden_slips(string key, int columns)
    {
        await BusyDay();
        var result = await Run(key, Settled);
        var doc = ReportSlip.Build(result, "Test", TimeZoneInfo.FindSystemTimeZoneById("Africa/Nairobi"), columns);
        Tausi.Tests.Printing.SlipsTests.Golden($"report-{key}-{columns}", doc);
    }

    // ---- Ticket detail ----

    [Fact]
    public async Task The_ticket_detail_has_every_line_print_payment_movement_and_entry()
    {
        var day = await BusyDay();
        F.Approval.ApprovalToken = null;
        var detail = await F.Get<TicketDetailReport>().Execute(day.Edna1, None);

        detail.Lines.Select(l => l.Status).Should().Contain(["Voided", "Gift", "Printed"]);
        detail.Lines.Single(l => l.Name == "Pool Table").PriceChanges.Should().ContainSingle().Which.ApprovedBy.Should().Be("Administrator");
        detail.Prints.Select(p => p.Kind).Should().Contain(["Bill", "Dispense", "Void", "Receipt"]);
        detail.Prints.Should().Contain(p => p.Label.StartsWith("Corrected bill"));
        var jobs = Jobs(day.Edna1);
        foreach (var print in detail.Prints)
        {
            var job = jobs.Single(j => j.Id == print.Id);
            print.Text.Should().Be(job.TextPreview, "the slip text is exactly what was queued");
            job.Content.Should().NotBeEmpty();
        }
        detail.Prints.Single(p => p.Kind == "Void").Text.Should().Contain("Guiness");
        detail.Prints.Should().OnlyContain(p => p.By != null);
        detail.Payments.Select(p => p.Type).Should().BeEquivalentTo(["Cash", "M-Pesa"]);
        detail.Movements.Should().Contain(m => m.Kind == "Sale");
        detail.Journal.Select(e => e.Memo).Should().Contain(m => m.StartsWith("Receipt")).And.Contain(m => m.StartsWith("Cost of sales"))
            .And.Contain(m => m.StartsWith("Voided pour"));
        detail.Journal.Should().OnlyContain(e => e.Lines.Sum(l => l.Debit) == e.Lines.Sum(l => l.Credit));
        // In time order (the fake clock gives several events the same time).
        detail.Timeline.Select(e => e.Kind).Should().ContainInOrder("Opened", "Added", "Print", "Void", "Gift", "Payment", "Settled")
            .And.Contain(["Price change", "Discount"]);
        detail.Timeline.Should().BeInAscendingOrder(e => e.At);
        detail.Timeline.First(e => e.Kind == "Added" && e.Text.Contains("Tusker")).Text.Should().StartWith("3 ×", "as added, before the gift split it");

        var merged = await F.Get<TicketDetailReport>().Execute(day.Merged3, None);
        merged.Ticket.MergedInNumbers.Should().ContainSingle();
        merged.Movements.Should().Contain(m => m.Kind == "Void returned");
        merged.Timeline.Should().Contain(e => e.Kind == "Merged");

        var pdf = await F.Get<TicketDetailReport>().Pdf(day.Edna1, None);
        Encoding.Latin1.GetString(pdf.Content).Should().StartWith("%PDF");
    }

    [Fact]
    public async Task Old_slip_text_is_cleared_but_the_print_stays()
    {
        var day = await BusyDay();
        await F.SetSetting(SettingsRegistry.PrintKeepSlipTextDays, 30);
        foreach (var job in F.Db.PrintJobs.Where(j => j.TicketId == day.Edna1)) job.Status = PrintJobStatus.Sent;
        await F.Db.SaveChangesAsync();

        (await HousekeepingService.ClearOldSlipText(F.Db, F.Clock.GetUtcNow().AddDays(29), None)).Should().Be(0);
        var cleared = await HousekeepingService.ClearOldSlipText(F.Db, F.Clock.GetUtcNow().AddDays(31), None);
        cleared.Should().Be(Jobs(day.Edna1).Count);
        var detail = await F.Get<TicketDetailReport>().Execute(day.Edna1, None);
        detail.Prints.Should().NotBeEmpty().And.OnlyContain(p => p.Text == null);
    }
}
