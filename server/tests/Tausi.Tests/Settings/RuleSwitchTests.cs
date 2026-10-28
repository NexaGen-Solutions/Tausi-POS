using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Approvals;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Settings;
using Tausi.Application.Settling;
using Tausi.Application.Tickets;
using Tausi.Application.Terminals;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;
using Tausi.Domain.Settings;
using Tausi.Tests.Tickets;

namespace Tausi.Tests.Settings;

/// <summary>Each switch in the settings screen's Rules list changes what the server does.</summary>
public class RuleSwitchTests : ApprovalsFixtureBase
{
    /// <summary>edna's printed ticket on the table, opened by CAROL on Counter A with her shift started.</summary>
    private async Task<TicketDetailDto> ForCarol(string table, params (string Item, decimal Qty)[] lines)
    {
        var ticket = await Printed(table, lines);
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        await StartShift();
        Later();
        return ticket;
    }

    private Task<TicketDetailDto> Pay(Guid ticketId, PaymentType type, decimal? tendered = null, string? code = null, Guid? broughtBy = null) =>
        F.Get<PaymentCommands>().Add(ticketId, new AddPaymentRequest(type, null, tendered, code, null, broughtBy), None);

    // ---- pos.approvalsFromOwnCounter ----

    [Fact]
    public async Task With_requests_off_nobody_can_send_one_but_a_waiting_one_can_still_be_decided()
    {
        var sam = await F.LoginOn("SAM", "Counter B");
        await F.LoginOn("edna", "Pillar 1");
        var ticket = await Printed("7", ("Tusker Lager", 2), ("Guiness", 1));
        var waiting = await Request(ApprovalKind.Void, ticket.Id, LineOf(ticket, "Tusker Lager"), new VoidLineRequest(1, "Wrong item"), sam.UserId);

        await F.SetSetting(SettingsRegistry.PosApprovalsFromOwnCounter, false);

        var act = () => Request(ApprovalKind.Void, ticket.Id, LineOf(ticket, "Guiness"), new VoidLineRequest(1, "Wrong item"));
        (await act.Should().ThrowAsync<ForbiddenException>()).Which.Code.Should().Be(ApprovalCodes.RequestsOff);
        F.ActAs(sam);
        (await Approve(waiting.Id)).Status.Should().Be("Approved");
        (await F.Get<GetDeviceInfo>().Execute(None)).Rules!.ApprovalsFromOwnCounter.Should().BeFalse("terminals hide the request option");
    }

    // ---- payments.recordBroughtBy ----

    [Fact]
    public async Task With_brought_by_off_the_opener_is_recorded_whoever_is_sent_and_the_receipt_has_no_brought_by_line()
    {
        await F.SetSetting(SettingsRegistry.PaymentsRecordBroughtBy, false);
        var ticket = await ForCarol("7", ("Tusker Lager", 2));

        await Pay(ticket.Id, PaymentType.Cash, tendered: 500m, broughtBy: F.User("CYNTHIA").Id);
        await F.Get<SettleTicket>().Execute(ticket.Id, None);

        F.Read().Payments.Single(p => p.TicketId == ticket.Id).BroughtByUserId.Should().Be(F.User("edna").Id);
        var receipt = Jobs(ticket.Id).Single(j => j.Kind == PrintJobKind.Receipt).TextPreview;
        receipt.Should().Contain("Served by edna").And.NotContain("brought by");
    }

    [Fact]
    public async Task With_brought_by_on_the_cashiers_choice_is_kept_and_printed()
    {
        var ticket = await ForCarol("7", ("Tusker Lager", 2));
        await Pay(ticket.Id, PaymentType.Cash, tendered: 500m, broughtBy: F.User("CYNTHIA").Id);
        await F.Get<SettleTicket>().Execute(ticket.Id, None);

        F.Read().Payments.Single(p => p.TicketId == ticket.Id).BroughtByUserId.Should().Be(F.User("CYNTHIA").Id);
        Jobs(ticket.Id).Single(j => j.Kind == PrintJobKind.Receipt).TextPreview.Should().Contain("Cash brought by CYNTHIA");
    }

    // ---- print.showVoidsOnReceipt ----

    [Fact]
    public async Task With_voids_shown_the_receipt_lists_the_voided_item_at_0()
    {
        await F.SetSetting(SettingsRegistry.PrintShowVoidsOnReceipt, true);
        var ticket = await Printed("7", ("Tusker Lager", 2), ("Guiness", 1));
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);
        await Void(ticket.Id, LineOf(ticket, "Guiness"));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        await StartShift();
        Later();

        await Pay(ticket.Id, PaymentType.Cash, tendered: 500m);
        await F.Get<SettleTicket>().Execute(ticket.Id, None);

        var receipt = Jobs(ticket.Id).Single(j => j.Kind == PrintJobKind.Receipt).TextPreview;
        receipt.Should().Contain("1 x Guiness (Cold) VOID").And.Contain("KSh 500");
    }

    // ---- tickets.closeVoidedWithoutReceipt ----

    private async Task<TicketDetailDto> FullyVoided()
    {
        var ticket = await Printed("7", ("Tusker Lager", 1), ("Guiness", 1));
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);
        await Void(ticket.Id, LineOf(ticket, "Tusker Lager"));
        await Void(ticket.Id, LineOf(ticket, "Guiness"));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(ticket.Id);
        return ticket;
    }

    [Fact]
    public async Task A_fully_voided_ticket_closes_with_no_receipt_and_no_shift()
    {
        var ticket = await FullyVoided();
        CloseVoidedTicket.IsFullyVoided(F.Ticket(ticket.Number)).Should().BeTrue();

        await F.Get<CloseVoidedTicket>().Execute(ticket.Id, None);

        var closed = F.Ticket(ticket.Number);
        closed.Status.Should().Be(TicketStatus.Cancelled);
        closed.ReceiptNumber.Should().BeNull();
        Jobs(ticket.Id).Should().NotContain(j => j.Kind == PrintJobKind.Receipt);
        F.Read().JournalEntries.Should().NotContain(e => e.SourceId == ticket.Id.ToString());
        F.Read().AuditEntries.Should().Contain(a => a.Action == Application.Auditing.AuditActions.TicketClosedVoided);
    }

    [Fact]
    public async Task Close_ticket_is_refused_when_something_is_left_to_pay_when_switched_off_and_without_ticket_settle()
    {
        var open = await Printed("8", ("Tusker Lager", 1));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(open.Id);
        (await FluentActions.Awaiting(() => F.Get<CloseVoidedTicket>().Execute(open.Id, None)).Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be(SettleCodes.NotFullyVoided);

        var voided = await FullyVoided();
        await F.SetSetting(SettingsRegistry.TicketsCloseVoidedWithoutReceipt, false);
        (await FluentActions.Awaiting(() => F.Get<CloseVoidedTicket>().Execute(voided.Id, None)).Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be(SettleCodes.CloseVoidedOff);

        await F.SetSetting(SettingsRegistry.TicketsCloseVoidedWithoutReceipt, true);
        await F.LoginOn("edna", "Counter A");
        await TakeLock(voided.Id);
        await FluentActions.Awaiting(() => F.Get<CloseVoidedTicket>().Execute(voided.Id, None))
            .Should().ThrowAsync<Application.Security.PermissionDeniedException>();
    }

    // ---- payments.mpesaCodeRequired ----

    [Fact]
    public async Task With_the_code_optional_m_pesa_without_a_code_settles_and_shows_no_code_but_a_typed_code_is_still_checked()
    {
        var first = await ForCarol("7", ("Tusker Lager", 2));
        await FluentActions.Awaiting(() => Pay(first.Id, PaymentType.Mpesa)).Should().ThrowAsync<DomainException>("the code is required by default");

        await F.SetSetting(SettingsRegistry.PaymentsMpesaCodeRequired, false);
        await Pay(first.Id, PaymentType.Mpesa);
        await F.Get<SettleTicket>().Execute(first.Id, None);
        Jobs(first.Id).Single(j => j.Kind == PrintJobKind.Receipt).TextPreview.Should().Contain("M-Pesa No code");

        await F.LoginOn("edna", "Pillar 1");
        var second = await Printed("8", ("Tusker Lager", 1), ("Guiness", 1));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(second.Id);
        Later();
        await FluentActions.Awaiting(() => Pay(second.Id, PaymentType.Mpesa, code: "BAD")).Should().ThrowAsync<DomainException>();
        await Pay(second.Id, PaymentType.Mpesa, code: "SJK3ABC123");

        await F.LoginOn("edna", "Pillar 1");
        var third = await Printed("9", ("Guiness", 1));
        await F.LoginOn("CAROL", "Counter A");
        await TakeLock(third.Id);
        var used = () => Pay(third.Id, PaymentType.Mpesa, code: "SJK3ABC123");
        (await used.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(SettleCodes.MpesaCodeUsed);

        var shift = await F.Get<Application.Cash.ShiftQueries>().Current(None);
        shift.Shift!.MpesaNoCodeCount.Should().Be(1);
    }

    // ---- stock.returnOnVoidIfNotServed ----

    [Fact]
    public async Task With_stock_return_off_every_void_counts_as_poured()
    {
        await F.SetSetting(SettingsRegistry.StockReturnOnVoidIfNotServed, false);
        var ticket = await Printed("7", ("Tusker Lager", 2));
        await F.LoginOn("SAM", "Counter A");
        await TakeLock(ticket.Id);

        await Void(ticket.Id, LineOf(ticket, "Tusker Lager"), poured: false);

        F.Read().VoidLogs.Single().WasPoured.Should().BeTrue();
    }

    // ---- print.mode ----

    [Fact]
    public async Task In_send_and_bill_mode_the_one_button_print_is_refused_and_send_and_print_bill_work()
    {
        await F.SetSetting(SettingsRegistry.PrintModeKey, nameof(PrintMode.SendAndBill));
        var ticket = await F.NewTicket("7", ("Tusker Lager", 1));

        (await FluentActions.Awaiting(() => F.Get<PrintTicket>().Print(ticket.Id, None)).Should().ThrowAsync<ConflictException>())
            .Which.Code.Should().Be(PrintCodes.SendAndBillMode);
        await F.Get<PrintTicket>().Send(ticket.Id, None);
        Later();
        await F.Get<PrintTicket>().PrintBill(ticket.Id, None);
        Jobs(ticket.Id).Select(j => j.Kind).Should().Equal(PrintJobKind.Dispense, PrintJobKind.Bill);
        (await F.Get<GetDeviceInfo>().Execute(None)).Printing!.Mode.Should().Be("SendAndBill");
    }

    // ---- pos.idleLogoutEnabled ----

    [Fact]
    public async Task With_idle_logout_off_a_session_lasts_until_the_twelve_hour_limit()
    {
        await F.SetSetting(SettingsRegistry.PosIdleLogoutEnabled, false);
        var edna = await F.LoginOn("edna", "Counter A");
        var sessions = F.Get<SessionKeeper>();

        F.Clock.Advance(TimeSpan.FromHours(3));
        (await sessions.Check(edna.SessionId!.Value, edna.UserId, edna.TerminalId!.Value, false, None)).Should().BeNull();
        F.Clock.Advance(TimeSpan.FromHours(9));
        (await sessions.Check(edna.SessionId!.Value, edna.UserId, edna.TerminalId!.Value, false, None)).Should().Be(SessionEndReasons.Expired);
        (await PosSettings.Load(F.Db, None)).IdleLogoutMinutesIfOn.Should().BeNull("terminals stop counting down");
    }
}
