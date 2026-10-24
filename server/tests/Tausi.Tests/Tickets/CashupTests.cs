using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Cash;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Security;
using Tausi.Application.Settling;
using Tausi.Application.Tickets;
using Tausi.Domain.Cash;
using Tausi.Domain.Common;
using Tausi.Domain.Ledger;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

public class CashupTests : ApprovalsFixtureBase
{
    private Task<SettleResult> Settle(Guid id) => F.Get<SettleTicket>().Execute(id, None);

    private Task<TicketDetailDto> Pay(Guid id, PaymentType type, decimal? amount = null, decimal? tendered = null, string? code = null) =>
        F.Get<PaymentCommands>().Add(id, new AddPaymentRequest(type, amount, tendered, code), None);

    private Task<ShiftDto> Current() => F.Get<ShiftQueries>().Current(None).ContinueWith(t => t.Result.Shift!);

    private Task<ShiftDto> Count(Guid shiftId, decimal total) =>
        F.Get<CountShift>().Execute(shiftId, new CountShiftRequest(new CashCount(Total: total)), None);

    private async Task<ShiftDto> SignOff(Guid shiftId, string supervisor = "MAU")
    {
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.DayOpenClose, supervisor);
        return await F.Get<CloseShift>().Execute(shiftId, None);
    }

    /// <summary>A pay-out by the cashier, approved by MAU's PIN (cashiers need approval for cash.expense).</summary>
    private async Task<ShiftDto> PayOut(Guid shiftId, decimal amount, string category = "Transport", string note = "Taxi for ice")
    {
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.CashExpense, "MAU");
        return await F.Get<DrawerMovements>().PayOut(shiftId,
            new DrawerMovementRequest(amount, F.Read().ExpenseCategories.Single(c => c.Name == category).Id, note), None);
    }

    private Task<ShiftDto> Drop(Guid shiftId, decimal amount) =>
        F.Get<DrawerMovements>().Drop(shiftId, new DrawerMovementRequest(amount, null, "To the safe"), None);

    /// <summary>edna orders and prints on Pillar 1; CAROL opens it on Counter A to settle.</summary>
    private async Task<TicketDetailDto> ForCarol(string table, params (string Item, decimal Qty)[] lines)
    {
        await F.LoginOn("edna", "Pillar 1");
        var ticket = await Printed(table, lines);
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        return ticket;
    }

    /// <summary>The acceptance shift: float 2,000; cash 500 for 370 (plus M-Pesa 1,000); M-Pesa; card; pay out 200; drop 1,000.</summary>
    private async Task<ShiftDto> AcceptanceShift()
    {
        await F.LoginOn("CAROL", "Counter A");
        await StartShift(2000m);
        var first = await ForCarol("1", ("Tusker Lager", 2), ("Guiness", 1), ("Mau's Water 1L", 1), ("Sodas 300ml", 2), ("Pineapple Punch", 1));
        await Pay(first.Id, PaymentType.Mpesa, 1000m, code: "SJK3ABC123");
        await Pay(first.Id, PaymentType.Cash, tendered: 500m);
        await Settle(first.Id);
        var second = await ForCarol("2", ("Guiness", 1));
        await Pay(second.Id, PaymentType.Mpesa, 300m, code: "SJK3ABC124");
        await Settle(second.Id);
        var third = await ForCarol("3", ("Pineapple Punch", 1));
        await Pay(third.Id, PaymentType.Card, 270m);
        await Settle(third.Id);
        var shift = await Current();
        await PayOut(shift.Id, 200m);
        return await Drop(shift.Id, 1000m);
    }

    private JournalEntry[] Entries(Guid shiftId) =>
        F.Read().JournalEntries.Include(e => e.Lines).ThenInclude(l => l.Account).Where(e => e.ShiftId == shiftId).ToArray();

    private static decimal Sum(IEnumerable<JournalEntry> entries, string code, bool debit) =>
        entries.SelectMany(e => e.Lines).Where(l => l.Account!.Code == code).Sum(l => debit ? l.Debit : l.Credit);

    // ---- Shifts ----

    [Fact]
    public async Task One_shift_per_cashier_and_one_per_drawer()
    {
        await F.LoginOn("CAROL", "Counter A");
        await StartShift(2000m);

        await F.LoginOn("CAROL", "Counter B");
        await FluentActions.Awaiting(() => StartShift()).Should().ThrowAsync<ConflictException>()
            .Where(e => e.Code == ShiftCodes.AlreadyOpen && e.Message == "You already have a shift open on Counter A. Close it first.");
        await F.LoginOn("MAU", "Counter A");
        await FluentActions.Awaiting(() => StartShift()).Should().ThrowAsync<ConflictException>()
            .Where(e => e.Code == ShiftCodes.DrawerInUse && e.Message == "Counter A's drawer is in CAROL's shift. CAROL must close it first.");
        await F.LoginOn("edna", "Counter B");
        await FluentActions.Awaiting(() => StartShift()).Should().ThrowAsync<PermissionDeniedException>("waiters don't settle");
    }

    [Fact]
    public async Task A_shift_needs_an_open_day()
    {
        await using var noDay = await TicketsFixture.Create(openDay: false);
        await noDay.LoginOn("CAROL", "Counter A");
        await FluentActions.Awaiting(() => noDay.Get<OpenShift>().Execute(new OpenShiftRequest(), None))
            .Should().ThrowAsync<DomainException>().WithMessage("The day isn't open yet*");
    }

    [Fact]
    public async Task The_float_can_be_counted_by_note_and_defaults_to_the_setting()
    {
        var settings = F.Db.BusinessSettings.Single();
        settings.OtherJson = """{"cash":{"defaultFloat":1500}}""";
        await F.Db.SaveChangesAsync();
        F.Db.ChangeTracker.Clear();
        await F.LoginOn("CAROL", "Counter A");
        (await F.Get<ShiftQueries>().Current(None)).Start.DefaultFloat.Should().Be(1500m);
        (await F.Get<OpenShift>().Execute(new OpenShiftRequest(), None)).OpeningFloat.Should().Be(1500m);

        await F.LoginOn("MAU", "Counter B");
        var counted = await F.Get<OpenShift>().Execute(new OpenShiftRequest(
            new CashCount(new Dictionary<string, int> { ["1000"] = 1, ["500"] = 1, ["50"] = 3 }, 20m)), None);
        counted.OpeningFloat.Should().Be(1670m);
    }

    [Fact]
    public async Task Settling_needs_my_open_shift_on_this_terminal_and_payments_record_it()
    {
        var ticket = await ForCarol("1", ("Guiness", 1));
        await FluentActions.Awaiting(() => Pay(ticket.Id, PaymentType.Card, 300m)).Should().ThrowAsync<ConflictException>()
            .Where(e => e.Code == ShiftCodes.ShiftRequired);

        // A shift on another drawer doesn't count.
        await F.LoginOn("CAROL", "Counter B");
        var elsewhere = await StartShift();
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        await FluentActions.Awaiting(() => Pay(ticket.Id, PaymentType.Card, 300m)).Should().ThrowAsync<ConflictException>()
            .Where(e => e.Code == ShiftCodes.ShiftRequired);

        await F.LoginOn("CAROL", "Counter B");
        await Count(elsewhere.Id, 0m);
        await SignOff(elsewhere.Id);
        await F.LoginOn("CAROL", "Counter A");
        var shift = await StartShift();
        await TakeLock(ticket.Id);
        await Pay(ticket.Id, PaymentType.Card, 300m);
        await Settle(ticket.Id);
        F.Read().Payments.Single().ShiftId.Should().Be(shift.Id);
        F.Ticket(ticket.Number).SettledShiftId.Should().Be(shift.Id);
    }

    [Fact]
    public async Task Expected_cash_is_float_plus_cash_applied_minus_pay_outs_and_drops()
    {
        var shift = await AcceptanceShift();

        shift.Should().Match<ShiftDto>(s => s.OpeningFloat == 2000m && s.CashSettled == 370m && s.PayOuts == 200m && s.Drops == 1000m
            && s.MpesaCount == 2 && s.MpesaTotal == 1300m && s.CardCount == 1 && s.CardTotal == 270m);
        shift.Expected.Should().BeNull("the count is blind until submitted");
        var reader = F.Get<ShiftReader>();
        (await reader.Figures(shift.Id, None)).Expected(2000m).Should().Be(1170m);
        shift.Waiters.Single().Should().Match<WaiterCashDto>(w => w.Name == "edna" && w.Cash == 370m);

        // Cash on a part-paid ticket is in the drawer too.
        var part = await ForCarol("4", ("Guiness", 1));
        await Pay(part.Id, PaymentType.Cash, tendered: 100m);
        (await reader.Figures(shift.Id, None)).Expected(2000m).Should().Be(1270m);
    }

    // ---- Pay out and drop ----

    [Fact]
    public async Task Pay_outs_and_drops_cant_take_the_drawer_below_0_and_print_slips()
    {
        await F.LoginOn("CAROL", "Counter A");
        var shift = await StartShift(500m);
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.CashExpense, "MAU");
        await FluentActions.Awaiting(() => PayOut(shift.Id, 600m)).Should().ThrowAsync<DomainException>().Where(e => e.Code == ShiftCodes.BelowZero);
        await FluentActions.Awaiting(() => Drop(shift.Id, 501m)).Should().ThrowAsync<DomainException>().Where(e => e.Code == ShiftCodes.BelowZero);

        await F.Get<DrawerMovements>().PayOut(shift.Id, new DrawerMovementRequest(200m, F.Read().ExpenseCategories.Single(c => c.Name == "Transport").Id, "Taxi for ice"), None);
        await Drop(shift.Id, 300m);
        await FluentActions.Awaiting(() => Drop(shift.Id, 1m)).Should().ThrowAsync<DomainException>();

        var slips = F.Read().PrintJobs.Where(j => j.Kind == PrintJobKind.CashUp).OrderBy(j => j.CreatedAt).ToList();
        slips.Should().HaveCount(2);
        slips[0].TextPreview.Should().Contain("PAY OUT").And.Contain("Transport").And.Contain("Taxi for ice").And.Contain("Approved by").And.Contain("MAU");
        slips[1].TextPreview.Should().Contain("CASH DROP").And.Contain("300");
    }

    [Fact]
    public async Task A_pay_out_follows_cash_expense_allowed_needs_approval_or_not_allowed()
    {
        await F.LoginOn("CAROL", "Counter A");
        var shift = await StartShift(1000m);
        var transport = F.Read().ExpenseCategories.Single(c => c.Name == "Transport").Id;
        var request = new DrawerMovementRequest(100m, transport, "Taxi");

        await FluentActions.Awaiting(() => F.Get<DrawerMovements>().PayOut(shift.Id, request, None)).Should().ThrowAsync<ApprovalRequiredException>();
        await F.SetPermission("Cashier", PermissionKeys.CashExpense, PermissionValue.Allowed);
        (await F.Get<DrawerMovements>().PayOut(shift.Id, request, None)).PayOuts.Should().Be(100m);
        await F.SetPermission("Cashier", PermissionKeys.CashExpense, PermissionValue.NotAllowed);
        await FluentActions.Awaiting(() => F.Get<DrawerMovements>().PayOut(shift.Id, request, None)).Should().ThrowAsync<PermissionDeniedException>();
        await FluentActions.Awaiting(() => F.Get<DrawerMovements>().PayOut(shift.Id, request with { Note = " " }, None))
            .Should().ThrowAsync<Exception>();
    }

    // ---- Count and close ----

    [Fact]
    public async Task A_blind_count_shows_expected_and_difference_only_after_it_is_submitted_and_a_supervisor_signs_off()
    {
        var shift = await AcceptanceShift();
        shift.Blind.Should().BeTrue();
        shift.Expected.Should().BeNull();
        shift.Difference.Should().BeNull();

        // The supervisor sees both.
        var cashierSession = F.Session.User!;
        await F.LoginOn("MAU", "Counter B");
        (await F.Get<ShiftQueries>().Summary(shift.Id, None)).Expected.Should().Be(1170m);
        F.ActAs(cashierSession);

        var counted = await F.Get<CountShift>().Execute(shift.Id, new CountShiftRequest(
            new CashCount(new Dictionary<string, int> { ["1000"] = 1, ["100"] = 1, ["50"] = 1 }, 20m)), None);
        counted.Should().Match<ShiftDto>(s => s.Counted == 1170m && s.Expected == 1170m && s.Difference == 0m && s.CountSubmitted);
        await FluentActions.Awaiting(() => Count(shift.Id, 1200m)).Should().ThrowAsync<ConflictException>()
            .Where(e => e.Code == ShiftCodes.AlreadyCounted, "a blind count is submitted once");

        // CAROL's own PIN is refused, and so is a waiter's; MAU signs off.
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.DayOpenClose, "CAROL");
        await FluentActions.Awaiting(() => F.Get<CloseShift>().Execute(shift.Id, None)).Should().ThrowAsync<ApprovalRequiredException>().WithMessage("*own*");
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.DayOpenClose, "edna");
        await FluentActions.Awaiting(() => F.Get<CloseShift>().Execute(shift.Id, None)).Should().ThrowAsync<ApprovalRequiredException>();
        F.Approval.ApprovalToken = null;
        await FluentActions.Awaiting(() => F.Get<CloseShift>().Execute(shift.Id, None)).Should().ThrowAsync<ApprovalRequiredException>();

        var closed = await SignOff(shift.Id);
        closed.Should().Match<ShiftDto>(s => s.Status == "Closed" && s.SignedOffBy == "MAU" && s.Difference == 0m);
        var slip = F.Read().PrintJobs.Where(j => j.Kind == PrintJobKind.CashUp).AsEnumerable().OrderBy(j => j.CreatedAt).Last();
        slip.TextPreview.Should().Contain("CASH-UP · Counter A · CAROL").And.Contain("Signed off by MAU").And.Contain("Pay out · Transport")
            .And.Contain("Receipts").And.Contain("No. 1-3");

        // A closed shift can't be changed.
        await FluentActions.Awaiting(() => Count(shift.Id, 1m)).Should().ThrowAsync<ConflictException>().Where(e => e.Code == ShiftCodes.Closed);
        await FluentActions.Awaiting(() => Drop(shift.Id, 1m)).Should().ThrowAsync<ConflictException>();
        await FluentActions.Awaiting(() => SignOff(shift.Id)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Without_a_blind_count_the_cashier_sees_the_expected_cash_and_can_count_again()
    {
        var settings = F.Db.BusinessSettings.Single();
        settings.OtherJson = """{"cashup":{"blind":false}}""";
        await F.Db.SaveChangesAsync();
        F.Db.ChangeTracker.Clear();
        var shift = await AcceptanceShift();
        shift.Expected.Should().Be(1170m);
        (await Count(shift.Id, 1100m)).Difference.Should().Be(-70m);
        (await Count(shift.Id, 1170m)).Difference.Should().Be(0m);
    }

    [Fact]
    public async Task Every_drawer_entry_balances_and_a_short_count_goes_to_cash_over_short()
    {
        var shift = await AcceptanceShift();
        await Count(shift.Id, 1070m); // 100 short
        await SignOff(shift.Id);

        var entries = Entries(shift.Id);
        entries.Should().OnlyContain(e => e.Lines.Sum(l => l.Debit) == e.Lines.Sum(l => l.Credit));
        entries.Select(e => e.Memo).Should().Contain(["Opening float", "Pay out · Transport", "Cash drop", "Cash-up"]);
        Sum(entries, LedgerAccountCodes.CashOverShort, debit: true).Should().Be(100m);
        Sum(entries, LedgerAccountCodes.Expenses, debit: true).Should().Be(200m);
        // The drawer ends at 0: float in, the sales' cash (sales carry their shift), then pay-out, drop, short and the rest to the safe.
        entries.Should().Contain(e => e.SourceType == "Sale");
        (Sum(entries, LedgerAccountCodes.Cash, true) - Sum(entries, LedgerAccountCodes.Cash, false)).Should().Be(0m);
        Sum(entries, LedgerAccountCodes.Safe, true).Should().Be(1000m + 1070m);
        Sum(entries, LedgerAccountCodes.Safe, false).Should().Be(2000m);
    }

    [Fact]
    public async Task An_over_count_is_credited_to_cash_over_short()
    {
        await F.LoginOn("CAROL", "Counter A");
        var shift = await StartShift(1000m);
        await Count(shift.Id, 1050m);
        var closed = await SignOff(shift.Id);
        closed.Difference.Should().Be(50m);
        Sum(Entries(shift.Id), LedgerAccountCodes.CashOverShort, debit: false).Should().Be(50m);
    }

    [Fact]
    public async Task Cash_on_part_paid_tickets_must_be_settled_before_the_shift_closes()
    {
        await F.LoginOn("CAROL", "Counter A");
        var shift = await StartShift();
        var part = await ForCarol("4", ("Guiness", 1));
        await Pay(part.Id, PaymentType.Cash, tendered: 100m);
        await Count(shift.Id, 100m);
        await FluentActions.Awaiting(() => SignOff(shift.Id)).Should().ThrowAsync<ConflictException>().Where(e => e.Code == ShiftCodes.PendingCash);
    }

    // ---- Closing the day ----

    [Fact]
    public async Task Closing_the_day_is_blocked_by_open_tickets_and_shifts_then_expires_requests_prints_the_report_and_logs_everyone_out()
    {
        var shift = await AcceptanceShift();
        // edna's open ticket, and a request waiting on it.
        await F.LoginOn("edna", "Pillar 1");
        var open = await Printed("9", ("Guiness", 1));
        await Request(Tausi.Domain.Service.ApprovalKind.Void, open.Id, LineOf(open, "Guiness"), new VoidLineRequest(1, "Wrong item"));
        var mau = await F.LoginOn("MAU", "Counter B");

        var status = await F.Get<GetDayStatus>().Execute(None);
        status.Blockers.OpenTickets.Should().ContainSingle(k => k.Number == open.Number);
        status.Blockers.OpenShifts.Should().ContainSingle(s => s.Cashier == "CAROL");
        status.Blockers.PendingRequests.Should().HaveCount(1);
        status.Summary.Should().BeNull();
        await FluentActions.Awaiting(() => F.Get<CloseWorkPeriod>().Execute(None)).Should().ThrowAsync<OpenTicketsException>();

        // CAROL settles edna's ticket and cashes up.
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(open.Id);
        var carolSession = F.Session.User!;
        await Pay(open.Id, PaymentType.Card, 300m);
        await FluentActions.Awaiting(() => Settle(open.Id)).Should().ThrowAsync<ConflictException>("the void request still waits");
        F.ActAs(mau);
        await F.Get<Tausi.Application.Approvals.RejectRequest>().Execute(F.Read().ApprovalRequests.Single().Id, new(null), None);
        F.ActAs(carolSession);
        await Settle(open.Id);
        await Count(shift.Id, 1170m);
        await SignOff(shift.Id);

        F.ActAs(mau);
        var ready = await F.Get<GetDayStatus>().Execute(None);
        ready.Blockers.Blocked.Should().BeFalse();
        var summary = ready.Summary!;
        summary.Tickets.Should().Be(4);
        summary.PaymentsTotal.Should().Be(summary.Gross - summary.Discounts + summary.Rounding);
        (summary.Net + summary.Vat).Should().Be(summary.Gross - summary.Discounts);
        summary.PaymentsTotal.Should().Be(F.Read().Payments.Where(p => p.Status == PaymentStatus.Final).Sum(p => p.Amount));
        summary.Payments.Select(p => (p.Type, p.Count, p.Amount)).Should().BeEquivalentTo(
            new[] { ("Cash", 1, 370m), ("M-Pesa", 2, 1300m), ("Card", 2, 570m) });
        summary.PayOuts.Should().Be(200m);
        summary.ShiftDifferences.Single().Difference.Should().Be(0m);

        var closed = await F.Get<CloseWorkPeriod>().Execute(None);
        closed.LogoutAt.Should().Be(closed.ClosedAt + TimeSpan.FromSeconds(60));
        closed.Printed.Should().BeTrue();
        F.Read().PrintJobs.AsEnumerable().OrderBy(j => j.CreatedAt).Last().TextPreview.Should().Contain("DAY REPORT · Z").And.Contain("Closed by MAU");
        F.Events.Published.Should().Contain(e => e.Name == Tausi.Application.Events.PosEventNames.DayClosed);

        // Everyone is logged out once the warning is over.
        (await F.Get<EndSessionsAfterDayClose>().Run(None)).Should().Be(0, "the 60 seconds aren't over");
        F.Clock.Advance(TimeSpan.FromSeconds(61));
        (await F.Get<EndSessionsAfterDayClose>().Run(None)).Should().BeGreaterThan(0);
        F.Read().UserSessions.Should().OnlyContain(s => s.EndedAt != null);
        (await F.Get<EndSessionsAfterDayClose>().Run(None)).Should().Be(0, "it runs once per close");
    }

    [Fact]
    public async Task The_close_day_command_uses_the_same_checks_and_expires_pending_requests()
    {
        await F.LoginOn("CAROL", "Counter A");
        await StartShift();
        await FluentActions.Awaiting(() => F.Get<CloseWorkPeriod>().Close(null, "command", null, null, None))
            .Should().ThrowAsync<OpenTicketsException>().Where(e => e.Shifts.Count == 1);
        var shift = await Current();
        await Count(shift.Id, 0m);
        await SignOff(shift.Id);

        await F.LoginOn("edna", "Pillar 1");
        var ticket = await Printed("9", ("Guiness", 1));
        await Request(Tausi.Domain.Service.ApprovalKind.Reprint, ticket.Id, null, new ReprintRequest());
        await F.LoginOn("MAU", "Counter A");
        await TakeLock(ticket.Id);
        await Void(ticket.Id, LineOf(ticket, "Guiness"));
        // A ticket with nothing left keeps the day open until it is closed empty.
        await FluentActions.Awaiting(() => F.Get<CloseWorkPeriod>().Close(null, "command", null, null, None)).Should().ThrowAsync<OpenTicketsException>();
        await F.Get<CloseEmptyTicket>().Execute(ticket.Id, None);

        (await F.Get<CloseWorkPeriod>().Close(null, "command", null, null, None)).Should().NotBeNull();
        F.Read().ApprovalRequests.Should().OnlyContain(r => r.Status != ApprovalStatus.Pending);
    }

    // ---- My sales ----

    [Fact]
    public async Task My_sales_counts_only_my_lines_after_their_discount_with_voids_out_and_gifts_at_0()
    {
        // edna: 2 Tusker (500) and a punch (270) on Table 7; CYNTHIA adds a Guiness (300) to it.
        await F.LoginOn("edna", "Pillar 1");
        var ticket = await Printed("7", ("Tusker Lager", 2), ("Pineapple Punch", 1));
        await F.LoginOn("CYNTHIA", "Pillar 1");
        await TakeLock(ticket.Id);
        await F.Add(ticket.Id, "Guiness", 1, ["Cold"]);
        await F.Get<PrintTicket>().Print(ticket.Id, None);
        // MAU voids a Tusker, gifts the punch and gives 10% off.
        await F.LoginOn("MAU", "Counter A");
        await TakeLock(ticket.Id);
        await Void(ticket.Id, LineOf(ticket, "Tusker Lager"), 1);
        await Gift(ticket.Id, LineOf(ticket, "Pineapple Punch"), 1);
        await Discount(ticket.Id, DiscountKind.Percent, 10m);
        // CAROL takes 200 cash brought by edna and leaves it part paid.
        await F.LoginOn("CAROL", "Counter A");
        await StartShift();
        await TakeLock(ticket.Id);
        await Pay(ticket.Id, PaymentType.Cash, tendered: 200m);
        await Pay(ticket.Id, PaymentType.Card, 50m);

        await F.LoginOn("edna", "Pillar 1");
        var mine = await F.Get<GetMySales>().Execute(None);

        // 1 Tusker (250) less its share of 10% of 550 (250/550 × 55 = 25) = 225; punch is a gift (0).
        mine.Total.Should().Be(225m);
        mine.Tickets.Should().Be(1);
        mine.Lines.Should().HaveCount(3).And.OnlyContain(l => l.TicketNumber == ticket.Number);
        mine.Lines.Should().ContainSingle(l => l.Voided && l.Name == "Tusker Lager" && l.Amount == 250m);
        mine.Lines.Should().ContainSingle(l => l.Gift && l.Amount == 0m);
        mine.Lines.Should().NotContain(l => l.Name == "Guiness", "CYNTHIA added it");
        mine.Should().Match<MySalesDto>(m => m.Cash == 200m && m.Card == 50m && m.Mpesa == 0m);
        mine.OpenBills.Should().ContainSingle(b => b.Number == ticket.Number);

        await F.LoginOn("CYNTHIA", "Pillar 2");
        var hers = await F.Get<GetMySales>().Execute(None);
        hers.Lines.Should().OnlyContain(l => l.Name == "Guiness");
        hers.OpenBills.Should().BeEmpty("edna opened the ticket");
        hers.Cash.Should().Be(0m);
    }

    [Fact]
    public async Task My_sales_prints_the_waiter_slip()
    {
        await F.LoginOn("edna", "Counter A");
        await Printed("7", ("Guiness", 1));
        Later();
        await F.Get<PrintMySales>().Execute(None);
        var slip = F.Read().PrintJobs.AsEnumerable().OrderBy(j => j.CreatedAt).Last();
        slip.TextPreview.Should().Contain("MY SALES · edna").And.Contain("Bills still open").And.Contain("Table 7");
    }
}
