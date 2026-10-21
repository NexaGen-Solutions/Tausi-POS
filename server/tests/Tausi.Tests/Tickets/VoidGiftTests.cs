using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Approvals;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

public class VoidGiftTests : ApprovalsFixtureBase
{
    [Fact]
    public async Task A_supervisor_voids_directly_and_is_logged_as_requester_and_approver()
    {
        var ticket = await Printed("7", ("Tusker Lager", 2), ("Pineapple Punch", 1));
        var mau = await F.LoginOn("MAU", "Counter A");
        await TakeLock(ticket.Id);
        Later();

        var after = await Void(ticket.Id, LineOf(ticket, "Tusker Lager"), 1, "Wrong item");

        after.Total.Should().Be(ticket.Total - 250m);
        var tuskers = after.Lines.Where(l => l.Name == "Tusker Lager").ToList();
        tuskers.Should().HaveCount(2);
        tuskers.Single(l => l.Status == "Printed").Quantity.Should().Be(1);
        tuskers.Single(l => l.Status == "Voided").Should().Match<OrderLineDto>(l => l.Voided && l.Quantity == 1 && l.VoidReason == "Wrong item");
        var log = F.Read().VoidLogs.Single();
        log.Should().Match<VoidLog>(v => v.RequestedByUserId == mau.UserId && v.ApprovedByUserId == mau.UserId
            && v.Quantity == 1 && v.Amount == 250m && v.Reason == "Wrong item" && !v.WasPoured && v.UnitPrice == 250m);
    }

    [Fact]
    public async Task A_partial_void_keeps_who_added_it_the_terminal_time_price_and_print_round()
    {
        var ticket = await Printed("7", ("Tusker Lager", 3));
        var original = F.Ticket(ticket.Number).Lines.Single();
        await F.LoginOn("MAU", "Counter B");
        await TakeLock(ticket.Id);

        await Void(ticket.Id, original.Id, 2, "Customer changed mind", poured: true);

        var lines = F.Ticket(ticket.Number).Lines;
        var voided = lines.Single(l => l.Status == OrderLineStatus.Voided);
        voided.Should().Match<OrderLine>(l => l.Quantity == 2 && l.AddedByUserId == original.AddedByUserId
            && l.AddedTerminalId == original.AddedTerminalId && l.AddedAt == original.AddedAt && l.UnitPrice == 250m
            && l.PrintRound == 1 && l.Options == "Cold" && l.DispensePrinterId == original.DispensePrinterId);
        lines.Single(l => l.Id == original.Id).Quantity.Should().Be(1);
        F.Read().VoidLogs.Single().WasPoured.Should().BeTrue();
    }

    [Fact]
    public async Task Spilled_or_broken_is_always_poured_and_other_needs_a_note()
    {
        var ticket = await Printed("7", ("Tusker Lager", 2));
        await F.LoginOn("MAU", "Counter A");
        await TakeLock(ticket.Id);
        var line = LineOf(ticket, "Tusker Lager");

        await FluentActions.Awaiting(() => Void(ticket.Id, line, 1, "Other")).Should().ThrowAsync<DomainException>().WithMessage("*note*");
        await FluentActions.Awaiting(() => Void(ticket.Id, line, 1, "")).Should().ThrowAsync<DomainException>().WithMessage("Choose a reason.");
        await FluentActions.Awaiting(() => Void(ticket.Id, line, 3)).Should().ThrowAsync<DomainException>().WithMessage("*1 to 2*");
        await Void(ticket.Id, line, 1, "Spilled or broken", poured: false);
        await Void(ticket.Id, line, 1, "Other", note: "Glass had a crack");

        var logs = F.Read().VoidLogs.OrderBy(v => v.Reason).ToList();
        logs.Single(v => v.Reason == "Spilled or broken").WasPoured.Should().BeTrue();
        logs.Single(v => v.Reason == "Other: Glass had a crack").Note.Should().Be("Glass had a crack");
    }

    [Fact]
    public async Task Only_printed_lines_are_voided_and_a_voided_line_cant_be_voided_again()
    {
        var ticket = await Printed("7", ("Tusker Lager", 1));
        var withNew = await F.Add(ticket.Id, "Pineapple Punch");
        await F.LoginOn("MAU", "Counter A");
        await TakeLock(ticket.Id);

        await FluentActions.Awaiting(() => Void(ticket.Id, LineOf(withNew, "Pineapple Punch", "New")))
            .Should().ThrowAsync<DomainException>().WithMessage("*Remove*");
        var line = LineOf(ticket, "Tusker Lager");
        await Void(ticket.Id, line);
        await FluentActions.Awaiting(() => Void(ticket.Id, line)).Should().ThrowAsync<DomainException>().WithMessage("*already taken off*");
    }

    [Fact]
    public async Task The_void_slip_goes_to_the_dispense_printer_that_printed_the_line()
    {
        // Printed on Counter A (its dispense printer: Ticket Printer 1)...
        var ticket = await Printed("7", ("Guiness", 2));
        // ...voided on Pillar 1, whose own dispense printer is Pillar 1.
        await F.LoginOn("MAU", "Pillar 1");
        await TakeLock(ticket.Id);
        Later();

        await Void(ticket.Id, LineOf(ticket, "Guiness"), 1, "Wrong item");

        var slip = Jobs(ticket.Id).Single(j => j.Kind == PrintJobKind.Void);
        slip.Printer!.Name.Should().Be("Ticket Printer 1");
        slip.TextPreview.Should().Contain("VOID").And.Contain("1 x Guiness").And.Contain("COLD")
            .And.Contain("Reason: Wrong item").And.Contain("By: MAU · Approved: MAU").And.Contain("Table 7 · #1001");
    }

    [Fact]
    public async Task A_corrected_bill_prints_on_the_requesting_terminal_only_when_a_bill_was_printed()
    {
        var unbilled = await F.NewTicket("8", ("Tusker Lager", 2));
        var sent = await F.Get<PrintTicket>().Send(unbilled.Id, None);
        var billed = await Printed("7", ("Tusker Lager", 2));
        await F.LoginOn("MAU", "Pillar 1");
        await TakeLock(sent.Id);
        await Void(sent.Id, LineOf(sent, "Tusker Lager"));
        await TakeLock(billed.Id);
        await Void(billed.Id, LineOf(billed, "Tusker Lager"));

        Jobs(sent.Id).Should().NotContain(j => j.Kind == PrintJobKind.Bill);
        var corrected = Jobs(billed.Id).Where(j => j.Kind == PrintJobKind.Bill).Last();
        corrected.Printer!.Name.Should().Be("Ticket Printer 1", "Pillar 1's receipts print at Counter A");
        corrected.TextPreview.Should().Contain("BILL 2").And.Contain("Corrected bill · replaces earlier bills")
            .And.Contain("1 x Tusker Lager (Cold)").And.Contain("TOTAL                              KSh 250")
            .And.Contain("Total includes VAT 16%: KSh 34.48");
    }

    [Fact]
    public async Task Totals_and_vat_are_right_after_a_void()
    {
        var ticket = await Printed("7", ("Tusker Lager", 2), ("Mau's Water 1L", 2));
        await F.LoginOn("MAU", "Counter A");
        await TakeLock(ticket.Id);

        await Void(ticket.Id, LineOf(ticket, "Tusker Lager"), 2);

        var t = F.Ticket(ticket.Number);
        t.Total.Should().Be(200m);
        t.Lines.Where(l => l.Counts).Sum(l => l.Vat).Should().Be(27.59m);
        t.Lines.Where(l => l.Counts).Sum(l => l.Net + l.Vat).Should().Be(200m);
    }

    [Fact]
    public async Task A_waiter_needs_a_supervisor_pin_and_their_own_pin_is_refused()
    {
        var ticket = await Printed("7", ("Tusker Lager", 2));
        var line = LineOf(ticket, "Tusker Lager");

        await FluentActions.Awaiting(() => Void(ticket.Id, line)).Should().ThrowAsync<ApprovalRequiredException>();

        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.OrderVoid, "edna");
        await FluentActions.Awaiting(() => Void(ticket.Id, line)).Should().ThrowAsync<ApprovalRequiredException>()
            .WithMessage("*own*");

        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.OrderVoid, "CAROL");
        await FluentActions.Awaiting(() => Void(ticket.Id, line)).Should().ThrowAsync<ApprovalRequiredException>(
            "a cashier only has NeedsApproval, so can't approve");

        F.Approval.ApprovalToken = await F.ApprovalToken(PermissionKeys.OrderVoid, "MAU");
        await Void(ticket.Id, line);
        var log = F.Read().VoidLogs.Single();
        log.RequestedByUserId.Should().Be(F.User("edna").Id);
        log.ApprovedByUserId.Should().Be(F.User("MAU").Id);
    }

    [Fact]
    public async Task A_gift_is_free_keeps_its_price_and_shows_gift_on_the_bill()
    {
        var ticket = await Printed("7", ("Guiness", 2), ("Pineapple Punch", 1));
        await F.LoginOn("MAU", "Counter A");
        await TakeLock(ticket.Id);
        Later();

        var after = await Gift(ticket.Id, LineOf(ticket, "Guiness"), 1, "Regular customer");

        after.Total.Should().Be(ticket.Total - 300m);
        var gift = F.Ticket(ticket.Number).Lines.Single(l => l.IsGift);
        gift.Should().Match<OrderLine>(l => l.Quantity == 1 && l.UnitPrice == 300m && l.Gross == 0 && l.Vat == 0 && l.Net == 0 && l.Counts);
        F.Read().GiftLogs.Single().Should().Match<GiftLog>(g => g.Amount == 300m && g.UnitPrice == 300m && g.Reason == "Regular customer");
        var bill = Jobs(ticket.Id).Last(j => j.Kind == PrintJobKind.Bill).TextPreview;
        bill.Should().MatchRegex(@"1 x Guiness \(Cold\) GIFT +0").And.Contain("Corrected bill");

        await FluentActions.Awaiting(() => Gift(ticket.Id, gift.Id)).Should().ThrowAsync<DomainException>().WithMessage("*gift*");
    }

    [Fact]
    public async Task New_lines_can_be_gifted_and_print_on_the_dispense_slip()
    {
        var ticket = await F.NewTicket("7", ("Guiness", 1));
        await F.LoginOn("MAU", "Counter A");
        await TakeLock(ticket.Id);

        var after = await Gift(ticket.Id, LineOf(ticket, "Guiness", "New"));

        after.Lines.Single().Should().Match<OrderLineDto>(l => l.IsGift && l.Status == "New" && l.Gross == 0);
        after.Total.Should().Be(0);
    }

    [Fact]
    public async Task A_waiter_cant_gift_at_all()
    {
        var ticket = await Printed("7", ("Guiness", 1));
        await FluentActions.Awaiting(() => Gift(ticket.Id, LineOf(ticket, "Guiness"))).Should().ThrowAsync<PermissionDeniedException>();
    }
}
