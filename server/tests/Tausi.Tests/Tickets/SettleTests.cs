using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Approvals;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Application.Settling;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.Money;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

public class SettleTests : ApprovalsFixtureBase
{
    /// <summary>
    /// The wireframe's Table 7 for KSh 1,370, ordered and printed by edna on Counter A, then opened
    /// by CAROL (cashier) on Counter A.
    /// </summary>
    private async Task<TicketDetailDto> Table7ForCarol(string table = "7")
    {
        await F.Db.MenuItems.Where(i => i.Name == "House Water 1L")
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.VatExempt, true).SetProperty(i => i.VatRate, 0m));
        var ticket = await Printed(table, ("Tusker Lager", 2), ("Guiness", 1), ("House Water 1L", 1), ("Sodas 300ml", 2), ("Pineapple Punch", 1));
        ticket.Total.Should().Be(1370m);
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        if ((await F.Get<Tausi.Application.Cash.ShiftQueries>().Current(None)).Shift is null) await StartShift();
        Later();
        return ticket;
    }

    private Task<TicketDetailDto> Pay(Guid ticketId, PaymentType type, decimal? amount = null, decimal? tendered = null,
        string? code = null, string? reference = null, Guid? broughtBy = null) =>
        F.Get<PaymentCommands>().Add(ticketId, new AddPaymentRequest(type, amount, tendered, code, reference, broughtBy), None);

    private Task<SettleResult> Settle(Guid ticketId) => F.Get<SettleTicket>().Execute(ticketId, None);

    private JournalEntry Entry(Guid ticketId) =>
        F.Read().JournalEntries.Include(e => e.Lines).ThenInclude(l => l.Account).Single(e => e.SourceId == ticketId.ToString());

    private static decimal Dr(JournalEntry e, string code) => e.Lines.Where(l => l.Account!.Code == code).Sum(l => l.Debit);
    private static decimal Cr(JournalEntry e, string code) => e.Lines.Where(l => l.Account!.Code == code).Sum(l => l.Credit);

    // ---- Payments ----

    [Fact]
    public async Task Cash_applies_up_to_what_is_left_and_gives_change_and_a_part_payment_is_allowed()
    {
        var ticket = await Table7ForCarol();

        var part = await Pay(ticket.Id, PaymentType.Cash, tendered: 1000m);
        part.Payments!.Single().Should().Match<PaymentDto>(p => p.Amount == 1000m && p.Change == 0m && p.Status == "Pending");
        part.Remaining.Should().Be(370m);

        var rest = await Pay(ticket.Id, PaymentType.Cash, tendered: 500m);
        rest.Payments!.Last().Should().Match<PaymentDto>(p => p.Amount == 370m && p.Tendered == 500m && p.Change == 130m);
        rest.Remaining.Should().Be(0m);
        await FluentActions.Awaiting(() => Pay(ticket.Id, PaymentType.Cash, tendered: 10m)).Should().ThrowAsync<DomainException>().WithMessage("Nothing is left to pay.");
    }

    [Fact]
    public async Task M_pesa_codes_must_be_well_formed_and_never_used_before_even_on_a_removed_payment()
    {
        await F.Db.BusinessSettings.ExecuteUpdateAsync(u => u.SetProperty(b => b.TimeZone, "Africa/Nairobi"));
        var ticket = await Table7ForCarol();
        await FluentActions.Awaiting(() => Pay(ticket.Id, PaymentType.Mpesa, 100m, code: "SJK3")).Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == SettleCodes.MpesaCodeInvalid);
        await FluentActions.Awaiting(() => Pay(ticket.Id, PaymentType.Mpesa, 100m, code: "SJK3-ABC12")).Should().ThrowAsync<DomainException>();

        var added = await Pay(ticket.Id, PaymentType.Mpesa, 100m, code: "sjk3abc123");
        added.Payments!.Single().MpesaCode.Should().Be("SJK3ABC123");
        await F.Get<PaymentCommands>().Remove(ticket.Id, added.Payments!.Single().Id, None);

        // On another ticket, even though the first payment was removed.
        await F.LoginOn("edna", "Counter A");
        var other = await Printed("8", ("Guiness", 1));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(other.Id);
        var refused = await FluentActions.Awaiting(() => Pay(other.Id, PaymentType.Mpesa, 100m, code: "SJK3ABC123")).Should().ThrowAsync<ConflictException>();
        refused.Which.Code.Should().Be(SettleCodes.MpesaCodeUsed);
        refused.Which.Message.Should().Be($"Code SJK3ABC123 was already used on #{ticket.Number} (Table 7, 21:01).");
    }

    [Fact]
    public async Task M_pesa_and_card_cant_be_more_than_what_is_left()
    {
        var ticket = await Table7ForCarol();
        await FluentActions.Awaiting(() => Pay(ticket.Id, PaymentType.Mpesa, 1371m, code: "SJK3ABC123")).Should().ThrowAsync<DomainException>()
            .Where(e => e.Code == SettleCodes.Overpaid);
        await FluentActions.Awaiting(() => Pay(ticket.Id, PaymentType.Card, 1371m)).Should().ThrowAsync<DomainException>();
        (await Pay(ticket.Id, PaymentType.Card, reference: "1234")).Payments!.Single().Amount.Should().Be(1370m, "the amount defaults to what's left");
    }

    [Fact]
    public async Task Brought_by_defaults_to_whoever_opened_the_ticket_and_must_be_a_real_user()
    {
        var ticket = await Table7ForCarol();
        var paid = await Pay(ticket.Id, PaymentType.Cash, tendered: 100m);
        paid.Payments!.Single().BroughtBy.Should().Be("edna");
        paid.Payments!.Single().TakenBy.Should().Be("CAROL");
        await FluentActions.Awaiting(() => Pay(ticket.Id, PaymentType.Cash, tendered: 100m, broughtBy: Guid.NewGuid()))
            .Should().ThrowAsync<DomainException>().WithMessage("Choose who brought it.");

        var choices = await F.Get<BroughtByChoices>().Execute(ticket.Id, None);
        choices.First().Should().Be(new BroughtByDto(F.User("edna").Id, "edna", "Waiter", true));
        choices.Select(c => c.Name).Should().Contain("CAROL");
    }

    [Fact]
    public async Task Pending_payments_can_be_removed_and_final_ones_cant()
    {
        var ticket = await Table7ForCarol();
        var paid = await Pay(ticket.Id, PaymentType.Card, 1370m);
        var removed = await F.Get<PaymentCommands>().Remove(ticket.Id, paid.Payments!.Single().Id, None);
        removed.Payments.Should().BeEmpty();
        removed.Remaining.Should().Be(1370m);
        F.Read().Payments.Single().Status.Should().Be(PaymentStatus.Removed);

        var again = await Pay(ticket.Id, PaymentType.Card, 1370m);
        await Settle(ticket.Id);
        await FluentActions.Awaiting(() => F.Get<PaymentCommands>().Remove(ticket.Id, again.Payments!.Single().Id, None))
            .Should().ThrowAsync<DomainException>();
        F.Read().Payments.Single(p => p.Id == again.Payments!.Single().Id).Status.Should().Be(PaymentStatus.Final);
    }

    // ---- Complete ----

    [Fact]
    public async Task Mixed_payments_settle_the_ticket_print_the_receipt_and_post_a_balanced_entry()
    {
        var ticket = await Table7ForCarol();
        await Pay(ticket.Id, PaymentType.Mpesa, 1000m, code: "SJK3ABC123");
        await Pay(ticket.Id, PaymentType.Cash, tendered: 500m);

        var result = await Settle(ticket.Id);

        result.Should().Match<SettleResult>(r => r.Change == 130m && r.Total == 1370m && r.ReceiptNumber == 1 && !r.LogOut);
        var settled = F.Ticket(ticket.Number);
        settled.Should().Match<Ticket>(k => k.Status == TicketStatus.Settled && k.ReceiptNumber == 1 && k.LockTerminalId == null
            && k.SettledByUserId == F.User("CAROL").Id && k.SettledAt != null);
        F.Read().Payments.Should().OnlyContain(p => p.Status == PaymentStatus.Final);

        var entry = Entry(ticket.Id);
        entry.Lines.Sum(l => l.Debit).Should().Be(entry.Lines.Sum(l => l.Credit));
        Dr(entry, LedgerAccountCodes.Cash).Should().Be(370m, "the cash applied, not the 500 tendered");
        Dr(entry, LedgerAccountCodes.MPesa).Should().Be(1000m);
        Cr(entry, LedgerAccountCodes.VatPayable).Should().Be(175.18m);
        Cr(entry, LedgerAccountCodes.Sales).Should().Be(1370m - 175.18m);
        entry.Memo.Should().Be($"Receipt 1 · Ticket #{ticket.Number}");
        entry.WorkPeriodId.Should().Be(F.Read().WorkPeriods.Single(w => w.IsOpen).Id);
        entry.CreatedByUserId.Should().Be(F.User("CAROL").Id);

        var receipt = Jobs(ticket.Id).Single(j => j.Kind == PrintJobKind.Receipt);
        receipt.Printer!.Name.Should().Be("Ticket Printer 1");
        receipt.TextPreview.Should().Contain("RECEIPT No. 1").And.Contain("M-Pesa SJK3ABC123").And.Contain("Cash 500 · Change 130")
            .And.Contain("Cash brought by edna").And.Contain("Cashier: CAROL");
        F.Events.Published.Should().Contain(e => e.Name == Tausi.Application.Events.PosEventNames.TicketChanged);
    }

    [Fact]
    public async Task Complete_is_refused_with_a_pending_request_with_unprinted_items_and_with_anything_left()
    {
        var ticket = await Table7ForCarol();
        await FluentActions.Awaiting(() => Settle(ticket.Id)).Should().ThrowAsync<ConflictException>()
            .Where(e => e.Code == SettleCodes.NotPaid && e.Message == "1,370 is still to pay.");

        await F.Add(ticket.Id, "Guiness", 1, ["Cold"]);
        await F.Add(ticket.Id, "Pineapple Punch");
        await Pay(ticket.Id, PaymentType.Card, 1940m);
        await FluentActions.Awaiting(() => Settle(ticket.Id)).Should().ThrowAsync<ConflictException>()
            .Where(e => e.Code == SettleCodes.Unprinted && e.Message == "2 items haven't been printed.");
        await F.Get<PrintTicket>().Print(ticket.Id, None);

        // edna asks SAM for a void: the ticket waits for it.
        var sam = await F.LoginOn("SAM", "Counter B");
        await F.LoginOn("edna", "Pillar 1");
        await Request(ApprovalKind.Void, ticket.Id, LineOf(await Detail(ticket.Id), "Guiness"), new VoidLineRequest(1, "Wrong item"), sam.UserId);
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        await FluentActions.Awaiting(() => Settle(ticket.Id)).Should().ThrowAsync<ConflictException>()
            .Where(e => e.Code == ApprovalCodes.RequestsPending && e.Message == "Waiting for SAM to approve a void on Guiness.");
        F.Ticket(ticket.Number).Status.Should().Be(TicketStatus.Open);
    }

    [Fact]
    public async Task A_settled_ticket_cant_be_settled_again()
    {
        var ticket = await Table7ForCarol();
        await Pay(ticket.Id, PaymentType.Card, 1370m);
        await Settle(ticket.Id);
        await FluentActions.Awaiting(() => Settle(ticket.Id)).Should().ThrowAsync<Exception>();
        F.Read().JournalEntries.Count(e => e.SourceId == ticket.Id.ToString()).Should().Be(1);
    }

    [Fact]
    public async Task Receipt_numbers_follow_their_own_sequence()
    {
        var first = await Table7ForCarol("7");
        await Pay(first.Id, PaymentType.Card, 1370m);
        (await Settle(first.Id)).ReceiptNumber.Should().Be(1);
        await F.LoginOn("edna", "Counter A");
        var second = await Printed("8", ("Guiness", 1));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(second.Id);
        await Pay(second.Id, PaymentType.Card, 300m);
        (await Settle(second.Id)).ReceiptNumber.Should().Be(2);
        F.Ticket(second.Number).Number.Should().BeGreaterThan(1000, "ticket numbers are separate");
    }

    // ---- Rounding and the ledger ----

    [Fact]
    public async Task Nearest_shilling_rounding_is_stored_and_posted_to_the_rounding_account()
    {
        var ticket = await Table7ForCarol();
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);
        await Discount(ticket.Id, DiscountKind.Percent, 7m, "Promotion"); // 1,370 - 95.90 = 1,274.10
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);

        var view = await Detail(ticket.Id);
        view.Should().Match<TicketDetailDto>(t => t.Total == 1274.10m && t.Rounding == -0.10m && t.AmountDue == 1274m && t.Remaining == 1274m);
        await Pay(ticket.Id, PaymentType.Cash, tendered: 1300m);
        (await Settle(ticket.Id)).Change.Should().Be(26m);

        F.Ticket(ticket.Number).RoundingAmount.Should().Be(-0.10m);
        var entry = Entry(ticket.Id);
        Dr(entry, LedgerAccountCodes.Rounding).Should().Be(0.10m);
        Dr(entry, LedgerAccountCodes.Cash).Should().Be(1274m);
        (Cr(entry, LedgerAccountCodes.Sales) + Cr(entry, LedgerAccountCodes.VatPayable)).Should().Be(1274.10m);
        entry.Lines.Sum(l => l.Debit).Should().Be(entry.Lines.Sum(l => l.Credit));
    }

    [Fact]
    public async Task With_no_rounding_the_cents_are_paid_exactly()
    {
        await F.Db.BusinessSettings.ExecuteUpdateAsync(u => u.SetProperty(b => b.Rounding, RoundingRule.None));
        var ticket = await Table7ForCarol();
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);
        await Discount(ticket.Id, DiscountKind.Percent, 7m, "Promotion");
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);

        (await Pay(ticket.Id, PaymentType.Card, 1274.10m)).Remaining.Should().Be(0m);
        await Settle(ticket.Id);
        Entry(ticket.Id).Lines.Should().NotContain(l => l.Account!.Code == LedgerAccountCodes.Rounding);
    }

    [Theory]
    [InlineData("cash")]
    [InlineData("mpesa")]
    [InlineData("card")]
    [InlineData("mixed")]
    [InlineData("discount")]
    [InlineData("gift")]
    [InlineData("void")]
    public async Task The_sale_entry_balances_and_sales_are_net_of_vat_after_discounts(string kind)
    {
        var ticket = await Table7ForCarol();
        if (kind is "discount" or "gift" or "void")
        {
            await F.LoginOn("SAM", "Counter A");
            await TakeLock(ticket.Id);
            var tusker = LineOf(ticket, "Tusker Lager");
            if (kind == "discount") await Discount(ticket.Id, DiscountKind.Amount, 137m, "Regular customer");
            if (kind == "gift") await Gift(ticket.Id, tusker, 1);
            if (kind == "void") await Void(ticket.Id, tusker, 1);
            await F.LoginOn("CAROL", "Counter A");
            await TakeLock(ticket.Id);
        }
        var due = (await Detail(ticket.Id)).AmountDue;
        switch (kind)
        {
            case "mpesa": await Pay(ticket.Id, PaymentType.Mpesa, due, code: "SJK3ABC999"); break;
            case "card": await Pay(ticket.Id, PaymentType.Card, due); break;
            case "mixed":
                await Pay(ticket.Id, PaymentType.Card, 200m);
                await Pay(ticket.Id, PaymentType.Mpesa, 500m, code: "SJK3ABC998");
                await Pay(ticket.Id, PaymentType.Cash, tendered: 1000m);
                break;
            default: await Pay(ticket.Id, PaymentType.Cash, tendered: due); break;
        }

        await Settle(ticket.Id);

        var lines = F.Ticket(ticket.Number).Lines.Where(l => l.Counts).ToList();
        var entry = Entry(ticket.Id);
        entry.Lines.Sum(l => l.Debit).Should().Be(entry.Lines.Sum(l => l.Credit)).And.Be(due);
        Cr(entry, LedgerAccountCodes.Sales).Should().Be(lines.Sum(l => l.Net));
        Cr(entry, LedgerAccountCodes.VatPayable).Should().Be(lines.Sum(l => l.Vat));
        // One sales line per VAT rate: 16% and exempt.
        entry.Lines.Where(l => l.Account!.Code == LedgerAccountCodes.Sales).Select(l => l.Memo).Should()
            .BeEquivalentTo(["Sales · VAT 16%", "Sales · VAT exempt"]);
        if (kind == "discount") Cr(entry, LedgerAccountCodes.Sales).Should().Be(lines.Sum(l => l.Gross - l.Discount) - lines.Sum(l => l.Vat));
    }

    // ---- Drawer, logout, permission ----

    [Fact]
    public async Task The_drawer_opens_only_on_cash_settles_and_only_when_the_printer_has_it_on()
    {
        var withCard = await Table7ForCarol("7");
        await Pay(withCard.Id, PaymentType.Card, 1370m);
        await Settle(withCard.Id);

        await F.Db.Printers.Where(p => p.Name == "Ticket Printer 1").ExecuteUpdateAsync(u => u.SetProperty(p => p.OpenDrawerOnReceipt, true));
        await F.LoginOn("edna", "Counter A");
        var withCashOff = await Printed("9", ("Guiness", 1));
        var withCash = await Printed("8", ("Guiness", 1));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(withCash.Id);
        await Pay(withCash.Id, PaymentType.Cash, tendered: 500m);
        await Settle(withCash.Id);

        bool Pulsed(Guid id) => Jobs(id).Single(j => j.Kind == PrintJobKind.Receipt).Content.AsSpan().IndexOf(ReceiptBuilder.DrawerPulse) >= 0;
        Pulsed(withCash.Id).Should().BeTrue();
        await F.Db.Printers.Where(p => p.Name == "Ticket Printer 1").ExecuteUpdateAsync(u => u.SetProperty(p => p.OpenDrawerOnReceipt, false));
        await TakeLock(withCashOff.Id);
        await Pay(withCashOff.Id, PaymentType.Cash, tendered: 300m);
        await Settle(withCashOff.Id);
        Pulsed(withCashOff.Id).Should().BeFalse("the printer has it off");
        Pulsed(withCard.Id).Should().BeFalse("no cash");

        // A reprint says COPY and never opens the drawer.
        await F.Db.Printers.Where(p => p.Name == "Ticket Printer 1").ExecuteUpdateAsync(u => u.SetProperty(p => p.OpenDrawerOnReceipt, true));
        await F.Get<ReprintReceipt>().Execute(withCash.Id, None);
        var copy = Jobs(withCash.Id).Where(j => j.Kind == PrintJobKind.Receipt).OrderBy(j => j.CreatedAt).Last();
        copy.TextPreview.Should().Contain("COPY");
        copy.Content.AsSpan().IndexOf(ReceiptBuilder.DrawerPulse).Should().Be(-1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Logout_after_settle_follows_the_setting(bool logout)
    {
        if (logout)
        {
            var settings = F.Db.BusinessSettings.Single();
            settings.OtherJson = """{"pos":{"logoutAfterSettle":true}}""";
            await F.Db.SaveChangesAsync();
            F.Db.ChangeTracker.Clear();
        }
        var ticket = await Table7ForCarol();
        var carol = F.Session.User!;
        await Pay(ticket.Id, PaymentType.Card, 1370m);

        (await Settle(ticket.Id)).LogOut.Should().Be(logout);
        F.Read().UserSessions.Single(s => s.Id == carol.SessionId).EndedAt.HasValue.Should().Be(logout);
    }

    [Fact]
    public async Task A_waiter_never_settles_even_with_needs_approval_and_a_supervisor_pin()
    {
        var ticket = await Printed("7", ("Guiness", 1));
        await F.SetPermission("Waiter", PermissionKeys.TicketSettle, PermissionValue.NeedsApproval);
        (await F.Get<IPermissionService>().Check(F.Session.User!, PermissionKeys.TicketSettle, None)).Should().Be(PermissionValue.NotAllowed);

        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.TicketSettle, "SAM");
        await FluentActions.Awaiting(() => Pay(ticket.Id, PaymentType.Card, 300m)).Should().ThrowAsync<PermissionDeniedException>();
        await FluentActions.Awaiting(() => Settle(ticket.Id)).Should().ThrowAsync<PermissionDeniedException>();
    }

    [Fact]
    public async Task The_settled_tab_lists_todays_receipts_newest_first()
    {
        var first = await Table7ForCarol("7");
        await Pay(first.Id, PaymentType.Mpesa, 1000m, code: "SJK3ABC123");
        await Pay(first.Id, PaymentType.Cash, tendered: 500m);
        await Settle(first.Id);

        var list = await F.Get<GetSettledTickets>().Execute(None);
        list.Single().Should().Match<SettledTicketDto>(s => s.ReceiptNumber == 1 && s.Where == "Table 7" && s.Total == 1370m
            && s.Cashier == "CAROL" && s.PaymentTypes.SequenceEqual(new[] { "Mpesa", "Cash" }));
        await F.LoginOn("edna", "Counter A");
        await FluentActions.Awaiting(() => F.Get<GetSettledTickets>().Execute(None)).Should().ThrowAsync<PermissionDeniedException>();
    }

    // ---- Closing the day ----

    [Fact]
    public async Task The_day_cant_close_with_open_or_part_paid_tickets_and_closes_once_they_are_settled()
    {
        var ticket = await Table7ForCarol();
        await Pay(ticket.Id, PaymentType.Cash, tendered: 1000m);
        await F.LoginOn("SAM", "Counter B");

        var refused = await FluentActions.Awaiting(() => F.Get<CloseWorkPeriod>().Execute(None)).Should().ThrowAsync<OpenTicketsException>();
        refused.Which.Tickets.Single().Should().Be(new OpenTicketDto(ticket.Id, ticket.Number, "Table 7", "edna", 1370m, 1000m));
        refused.Which.Message.Should().Contain($"#{ticket.Number} Table 7 · edna · 1,370 (part paid)");
        refused.Which.Shifts.Single().Should().Match<OpenShiftDto>(sh => sh.Cashier == "CAROL" && sh.Terminal == "Counter A");
        refused.Which.Message.Should().Contain("1 cashier shift is open: CAROL on Counter A");
        (await F.Get<GetTableMap>().Execute(null, None)).Tables.Single(t => t.Name == "7").Tickets.Single().Left.Should().Be(370m);

        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        await Pay(ticket.Id, PaymentType.Cash, tendered: 370m);
        await Settle(ticket.Id);
        // CAROL counts her drawer and SAM signs her shift off.
        var shift = (await F.Get<Tausi.Application.Cash.ShiftQueries>().Current(None)).Shift!;
        await F.Get<Tausi.Application.Cash.CountShift>().Execute(shift.Id,
            new Tausi.Application.Cash.CountShiftRequest(new Tausi.Application.Cash.CashCount(Total: 1370m)), None);
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.DayOpenClose, "SAM");
        await F.Get<Tausi.Application.Cash.CloseShift>().Execute(shift.Id, None);
        await F.LoginOn("SAM", "Counter B");
        (await F.Get<CloseWorkPeriod>().Execute(None)).Should().NotBeNull();
        F.Read().WorkPeriods.Should().OnlyContain(w => !w.IsOpen);
    }

    [Fact]
    public async Task Only_empty_tickets_are_closed_empty_and_only_with_day_open_close()
    {
        var ticket = await Printed("7", ("Guiness", 1));
        await FluentActions.Awaiting(() => F.Get<CloseEmptyTicket>().Execute(ticket.Id, None)).Should().ThrowAsync<PermissionDeniedException>();

        await F.LoginOn("SAM", "Counter A");
        await FluentActions.Awaiting(() => F.Get<CloseEmptyTicket>().Execute(ticket.Id, None)).Should().ThrowAsync<DomainException>()
            .WithMessage("*still has items*");
        await TakeLock(ticket.Id);
        await Void(ticket.Id, LineOf(ticket, "Guiness"), 1);

        await F.Get<CloseEmptyTicket>().Execute(ticket.Id, None);
        F.Ticket(ticket.Number).Status.Should().Be(TicketStatus.Cancelled);
        (await F.Get<CloseWorkPeriod>().Execute(None)).Should().NotBeNull();
    }
}
