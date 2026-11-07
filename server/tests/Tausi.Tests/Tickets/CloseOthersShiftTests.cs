using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Cash;
using Tausi.Application.Common;
using Tausi.Application.Day;
using Tausi.Application.Printing;
using Tausi.Application.Reports;
using Tausi.Application.Security;
using Tausi.Application.Settling;
using Tausi.Application.Tickets;
using Tausi.Domain.Cash;
using Tausi.Domain.Ledger;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

/// <summary>
/// Task 19 (v1.0.2): a supervisor closes a shift its cashier left open, so the day can close.
/// CAROL takes 370 cash on Counter A and leaves; SAM closes her shift from Counter B.
/// </summary>
public class CloseOthersShiftTests : ApprovalsFixtureBase
{
    private OthersShifts Others => F.Get<OthersShifts>();

    /// <summary>CAROL: float 2,000 and one cash sale of 370 on Counter A; then she leaves without closing. SAM logs in on Counter B.</summary>
    private async Task<Guid> CarolLeftHerShiftOpen(bool partPaid = false)
    {
        await F.LoginOn("edna", "Pillar 1");
        var ticket = await Printed("1", ("Pineapple Punch", 1), ("Sodas 300ml", 1));
        await F.LoginOn("CAROL", "Counter A");
        var shift = await StartShift(2000m);
        await TakeLock(ticket.Id);
        if (partPaid)
            await F.Get<PaymentCommands>().Add(ticket.Id, new AddPaymentRequest(PaymentType.Cash, Tendered: 100m), None);
        else
        {
            await F.Get<PaymentCommands>().Add(ticket.Id, new AddPaymentRequest(PaymentType.Cash, Tendered: 500m), None);
            await F.Get<SettleTicket>().Execute(ticket.Id, None);
        }
        await F.LoginOn("SAM", "Counter B");
        return shift.Id;
    }

    private Task<ShiftDto> Count(Guid id, decimal total, string reason = "Cashier left without closing", string? note = null) =>
        Others.Count(id, new CountOthersShiftRequest(new CashCount(Total: total), reason, note), None);

    [Fact]
    public void Supervisors_and_admins_may_by_default_and_it_can_not_be_approved()
    {
        Tausi.Infrastructure.Seeding.DefaultPermissions.For("Supervisor", PermissionKeys.CashCloseOthersShift).Should().Be(PermissionValue.Allowed);
        Tausi.Infrastructure.Seeding.DefaultPermissions.For("Admin", PermissionKeys.CashCloseOthersShift).Should().Be(PermissionValue.Allowed);
        Tausi.Infrastructure.Seeding.DefaultPermissions.For("Cashier", PermissionKeys.CashCloseOthersShift).Should().Be(PermissionValue.NotAllowed);
        Tausi.Infrastructure.Seeding.DefaultPermissions.For("Waiter", PermissionKeys.CashCloseOthersShift).Should().Be(PermissionValue.NotAllowed);
        PermissionCatalog.IsApprovable(PermissionKeys.CashCloseOthersShift).Should().BeFalse();
        PermissionCatalog.All.Single(p => p.Key == PermissionKeys.CashCloseOthersShift).Should().Match<PermissionInfo>(p =>
            p.Section == "Money" && p.Label == "Close another cashier's shift" && p.Help == "Count and close a shift that its cashier left open.");
    }

    [Fact]
    public async Task A_supervisor_closes_a_shift_left_open_from_another_terminal_with_another_users_pin_and_the_day_then_closes()
    {
        var id = await CarolLeftHerShiftOpen();
        var status = await F.Get<GetDayStatus>().Execute(None);
        status.CanCloseShifts.Should().BeTrue();
        status.Blockers.OpenShifts.Should().ContainSingle(s => s.Id == id && s.Cashier == "CAROL");

        var open = (await Others.List(None)).Single();
        open.Should().Match<OpenShiftRowDto>(s => s.Cashier == "CAROL" && s.Terminal == "Counter A" && s.CashTaken == 370 && !s.Counted);
        (await Others.View(id, None)).Expected.Should().BeNull("a blind count hides it from the person counting");
        await FluentActions.Awaiting(() => Count(id, 2270, reason: "")).Should().ThrowAsync<FieldErrorsException>();
        await FluentActions.Awaiting(() => Count(id, 2270, reason: "Other")).Should().ThrowAsync<FieldErrorsException>("Other needs a note");

        var counted = await Count(id, 2270);
        counted.Should().Match<ShiftDto>(s => s.Expected == 2370 && s.Difference == -100 && s.ClosedBy == "SAM" && s.CloseReason == "Cashier left without closing");
        await FluentActions.Awaiting(() => Others.Close(id, new CloseOthersShiftRequest(), None)).Should().ThrowAsync<ApprovalRequiredException>();

        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.DayOpenClose, "Administrator");
        var closed = await Others.Close(id, new CloseOthersShiftRequest(), None);
        closed.Should().Match<ShiftDto>(s => s.Status == "Closed" && s.SignedOffBy == "Administrator" && s.ClosedByOther && !s.CountedAlone);

        // As for a cashier's own close: the difference to over/short, the drawer to the safe, locked; every entry balances.
        var entries = F.Read().JournalEntries.Include(e => e.Lines).ThenInclude(l => l.Account).Where(e => e.ShiftId == id).ToList();
        entries.Should().OnlyContain(e => e.Lines.Sum(l => l.Debit) == e.Lines.Sum(l => l.Credit));
        var cashUp = entries.Single(e => e.Memo == "Cash-up");
        cashUp.Lines.Single(l => l.Account!.Code == LedgerAccountCodes.CashOverShort).Debit.Should().Be(100);
        cashUp.Lines.Where(l => l.Account!.Code == LedgerAccountCodes.Safe).Sum(l => l.Debit).Should().Be(2270);
        await FluentActions.Awaiting(() => Count(id, 1)).Should().ThrowAsync<ConflictException>("closed and locked");

        // The slip prints on SAM's terminal (Counter B), not the shift's.
        var slip = F.Read().PrintJobs.Where(j => j.Kind == Tausi.Domain.Printing.PrintJobKind.CashUp).ToList().Last();
        slip.TextPreview.Should().Contain("Shift of: CAROL").And.Contain("Closed by: SAM · Reason: Cashier left").And.Contain("without closing").And.NotContain("Counted alone");
        var counterB = F.Read().Terminals.Single(t => t.Name == "Counter B");
        if (counterB.ReceiptPrinterId is { } printer) slip.PrinterId.Should().Be(printer);

        var audit = F.Read().AuditEntries.Where(a => a.Action == "shift.closed").ToList().Last();
        audit.NewValueJson.Should().Contain("CAROL").And.Contain("Cashier left without closing").And.Contain("\"difference\":-100");

        // Nothing blocks the day now.
        (await F.Get<CloseWorkPeriod>().Execute(None)).Summary.Should().NotBeNull();

        // CAROL's notice at her next login, dismissed with OK.
        await F.LoginOn("CAROL", "Counter A");
        var notice = (await F.Get<ShiftNotices>().Mine(None)).Should().ContainSingle().Which;
        notice.Should().Match<ShiftNoticeDto>(n => n.Terminal == "Counter A" && n.ClosedBy == "SAM" && n.Difference == -100);
        await F.Get<ShiftNotices>().Dismiss(notice.ShiftId, None);
        (await F.Get<ShiftNotices>().Mine(None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Alone_the_closer_signs_off_with_the_tick_box_and_it_is_flagged_everywhere()
    {
        var id = await CarolLeftHerShiftOpen();
        await Count(id, 2370, reason: "End of day");

        // SAM's own PIN without the tick: someone else must sign off.
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.DayOpenClose, "SAM");
        var refused = await FluentActions.Awaiting(() => Others.Close(id, new CloseOthersShiftRequest(), None)).Should().ThrowAsync<ConflictException>();
        refused.Which.Code.Should().Be(ShiftCodes.SignOffNeeded);
        F.Approval.ApprovalToken = null;

        var closed = await Others.Close(id, new CloseOthersShiftRequest(CountedAlone: true), None);
        closed.Should().Match<ShiftDto>(s => s.SignedOffBy == "SAM" && s.CountedAlone);
        F.Read().PrintJobs.Where(j => j.Kind == Tausi.Domain.Printing.PrintJobKind.CashUp).ToList().Last().TextPreview
            .Should().Contain("Counted alone").And.Contain("Closed and signed off by the same person");
        F.Read().AuditEntries.Where(a => a.Action == "shift.closed").ToList().Last().NewValueJson
            .Should().Contain("Closed and signed off by the same person");
        var report = await F.Get<ReportService>().Run("payments", new ReportFilters(), None);
        var row = report.Tables.Single(t => t.Title == "Cashier shifts").Groups[0].Rows.Single();
        ((string)row.Cells[^1]!).Should().Contain("closed by SAM").And.Contain("Closed and signed off by the same person");
    }

    [Fact]
    public async Task Pending_payments_block_the_close_until_moved_to_the_closers_shift_or_removed()
    {
        var id = await CarolLeftHerShiftOpen(partPaid: true);
        var pending = (await Others.List(None)).Single().Pending.Should().ContainSingle().Which;
        pending.Should().Match<PendingPaymentDto>(p => p.Type == "Cash" && p.Amount == 100);
        await Count(id, 2000);
        await FluentActions.Awaiting(() => Others.Close(id, new CloseOthersShiftRequest(CountedAlone: true), None))
            .Should().ThrowAsync<ConflictException>().Where(e => e.Code == ShiftCodes.PendingPayments);

        // Moving needs SAM's own shift on this terminal.
        (await FluentActions.Awaiting(() => Others.MovePending(id, None)).Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be(ShiftCodes.NoShiftHere);
        var mine = await StartShift();
        (await Others.MovePending(id, None)).Should().BeEmpty();
        F.Read().Payments.Single(p => p.Id == pending.Id).ShiftId.Should().Be(mine.Id);
        (await Others.Close(id, new CloseOthersShiftRequest(CountedAlone: true), None)).Status.Should().Be("Closed");
    }

    [Fact]
    public async Task Removing_pending_payments_puts_the_ticket_back_to_unpaid()
    {
        var id = await CarolLeftHerShiftOpen(partPaid: true);
        var pending = (await Others.List(None)).Single().Pending.Single();
        (await Others.RemovePending(id, None)).Should().BeEmpty();
        F.Read().Payments.Single(p => p.Id == pending.Id).Status.Should().Be(PaymentStatus.Removed);
        F.Read().AuditEntries.Should().Contain(a => a.Action == "payment.removed");
        await Count(id, 2000);
        (await Others.Close(id, new CloseOthersShiftRequest(CountedAlone: true), None)).Status.Should().Be("Closed");
    }

    [Fact]
    public async Task Not_allowed_hides_it_and_the_server_refuses()
    {
        var id = await CarolLeftHerShiftOpen();
        await F.SetPermission("Supervisor", PermissionKeys.CashCloseOthersShift, PermissionValue.NotAllowed);
        (await F.Get<GetDayStatus>().Execute(None)).CanCloseShifts.Should().BeFalse();
        await FluentActions.Awaiting(() => Others.List(None)).Should().ThrowAsync<PermissionDeniedException>();
        await FluentActions.Awaiting(() => Count(id, 2370)).Should().ThrowAsync<PermissionDeniedException>();

        await F.LoginOn("CAROL", "Counter B");
        await FluentActions.Awaiting(() => Others.List(None)).Should().ThrowAsync<PermissionDeniedException>("a cashier never may");
    }

    [Fact]
    public async Task A_cashiers_own_shift_still_closes_as_before()
    {
        await F.LoginOn("CAROL", "Counter A");
        var shift = await StartShift(1000m);
        await F.Get<CountShift>().Execute(shift.Id, new CountShiftRequest(new CashCount(Total: 1000m)), None);
        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.DayOpenClose, "SAM");
        var closed = await F.Get<CloseShift>().Execute(shift.Id, None);
        closed.Should().Match<ShiftDto>(s => s.Status == "Closed" && !s.ClosedByOther && s.ClosedBy == "CAROL");
        (await F.Get<ShiftNotices>().Mine(None)).Should().BeEmpty("she closed it herself");
        F.Read().PrintJobs.Where(j => j.Kind == Tausi.Domain.Printing.PrintJobKind.CashUp).ToList().Last().TextPreview.Should().NotContain("Shift of:");
    }

    [Theory]
    [InlineData(false, 42)]
    [InlineData(false, 32)]
    [InlineData(true, 42)]
    [InlineData(true, 32)]
    public async Task The_cash_up_slip_of_a_shift_closed_by_someone_else_matches_its_golden_slip(bool alone, int columns)
    {
        var id = await CarolLeftHerShiftOpen();
        await Count(id, 2270);
        if (!alone) F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.DayOpenClose, "Administrator");
        var view = await Others.Close(id, new CloseOthersShiftRequest(CountedAlone: alone), None);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Nairobi");
        var doc = CashSlips.CashUp(view, "Test", at => TimeZoneInfo.ConvertTime(at, zone), new SlipStyle(columns, true, null, "KES"));
        Tausi.Tests.Printing.SlipsTests.Golden($"cashup-closed-by-other{(alone ? "-alone" : "")}-{columns}", doc);
    }
}
