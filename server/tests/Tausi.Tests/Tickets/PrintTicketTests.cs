using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;
using Tausi.Domain.Settings;

namespace Tausi.Tests.Tickets;

public class PrintTicketTests : IAsyncLifetime
{
    private TicketsFixture _f = null!;
    public async Task InitializeAsync() => _f = await TicketsFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<TicketDetailDto> Print(Guid id) => _f.Get<PrintTicket>().Print(id, CancellationToken.None);
    private Task<TicketDetailDto> Reprint(Guid id) => _f.Get<PrintTicket>().Reprint(id, CancellationToken.None);

    private List<PrintJob> Jobs(Guid ticketId) =>
        _f.Read().PrintJobs.Include(j => j.Printer).Where(j => j.TicketId == ticketId).AsEnumerable()
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id).ToList();

    /// <summary>edna on Counter A: 2 Tusker (Cold), 1 Pineapple Punch, 1 House Water on Table 4.</summary>
    private async Task<TicketDetailDto> Table4()
    {
        var ticket = await _f.Start("4", "Tusker Lager", 2, "Cold");
        await _f.Add(ticket.Id, "Pineapple Punch");
        return await _f.Add(ticket.Id, "House Water 1L");
    }

    [Fact]
    public async Task Print_queues_the_bill_with_every_line_first_then_the_dispense_slip_with_only_new_lines()
    {
        var ticket = await Table4();

        var printed = await Print(ticket.Id);

        printed.BillNumber.Should().Be(1);
        printed.NewLines.Should().Be(0);
        printed.Lines.Should().OnlyContain(l => l.Status == "Printed" && l.PrintRound == 1);
        var jobs = Jobs(ticket.Id);
        jobs.Select(j => j.Kind).Should().Equal(PrintJobKind.Bill, PrintJobKind.Dispense);
        jobs.Select(j => j.Printer!.Name).Should().Equal("Ticket Printer 1", "Ticket Printer 1");
        jobs[0].CreatedAt.Should().BeBefore(jobs[1].CreatedAt, "the bill always prints first");
        jobs[0].TextPreview.Should().Contain("BILL 1 · Ticket #1001").And.Contain("2 x Tusker Lager (Cold)")
            .And.Contain("1 x Pineapple Punch").And.Contain("1 x House Water 1L").And.Contain("Total includes VAT 16%")
            .And.NotContain("Replaces earlier bills");
        jobs[1].TextPreview.Should().Contain("COUNTER A").And.Contain("2 x Tusker Lager").And.Contain("COLD")
            .And.NotContain("KSh").And.NotContain("250");
        _f.Read().AuditEntries.Should().Contain(a => a.Action == AuditActions.TicketPrinted);
    }

    [Theory]
    [InlineData("Africa/Nairobi", "20 Oct 2026 21:00", "21:00")]
    [InlineData("America/New_York", "20 Oct 2026 14:00", "14:00")]
    public async Task Bill_and_slip_show_the_business_time_whatever_the_servers_time_zone(string zone, string billAt, string slipAt)
    {
        // The server's clock is in UTC (18:00); the business setting decides the printed time.
        _f.Clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        await _f.Db.BusinessSettings.ExecuteUpdateAsync(u => u.SetProperty(b => b.TimeZone, zone));
        var ticket = await Table4();

        await Print(ticket.Id);

        var jobs = Jobs(ticket.Id);
        jobs[0].TextPreview.Should().Contain(billAt).And.NotContain("18:00");
        jobs[1].TextPreview.Should().Contain(slipAt).And.NotContain("18:00");
    }

    [Fact]
    public async Task An_exempt_item_is_billed_as_vat_exempt_and_a_zero_rated_one_at_vat_0_percent()
    {
        await _f.Db.MenuItems.Where(i => i.Name == "House Water 1L")
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.VatExempt, true).SetProperty(i => i.VatRate, 0m));
        await _f.Db.MenuItems.Where(i => i.Name == "Pineapple Punch").ExecuteUpdateAsync(u => u.SetProperty(i => i.VatRate, 0m));
        var ticket = await Table4();

        var saved = _f.Ticket(ticket.Number).Lines;
        saved.Single(l => l.Name == "House Water 1L").Should().Match<OrderLine>(l => l.VatExempt && l.Vat == 0m && l.Net == l.Gross);
        saved.Single(l => l.Name == "Pineapple Punch").Should().Match<OrderLine>(l => !l.VatExempt && l.VatRate == 0m && l.Vat == 0m);

        await Print(ticket.Id);

        var bill = Jobs(ticket.Id)[0].TextPreview;
        bill.Should().Contain("Total includes VAT 16%: KSh 68.97").And.Contain("Total includes VAT 0%: KSh 0.00")
            .And.MatchRegex(@"VAT exempt: +KSh 100\.00");
    }

    [Fact]
    public async Task A_second_round_bills_everything_again_but_dispenses_only_the_new_line()
    {
        var ticket = await Table4();
        await Print(ticket.Id);
        _f.Clock.Advance(TimeSpan.FromSeconds(2));
        await _f.Add(ticket.Id, "Guiness", 1, ["Cold"]);

        var printed = await Print(ticket.Id);

        printed.BillNumber.Should().Be(2);
        var jobs = Jobs(ticket.Id).Skip(2).ToList();
        jobs[0].TextPreview.Should().Contain("BILL 2").And.Contain("Replaces earlier bills").And.Contain("Tusker").And.Contain("Guiness");
        jobs[1].TextPreview.Should().Contain("1 x Guiness").And.NotContain("Tusker").And.NotContain("Pineapple");
        _f.Read().OrderLines.Single(l => l.TicketId == ticket.Id && l.Name == "Guiness").PrintRound.Should().Be(2);
    }

    [Fact]
    public async Task With_nothing_new_print_is_refused_so_a_double_press_never_prints_twice()
    {
        var ticket = await Table4();
        await Print(ticket.Id);

        var again = await FluentActions.Awaiting(() => Print(ticket.Id)).Should().ThrowAsync<ConflictException>();

        again.Which.Code.Should().Be(PrintCodes.NothingNew);
        Jobs(ticket.Id).Should().HaveCount(2);
    }

    [Fact]
    public async Task If_a_job_can_not_be_created_nothing_is_marked_printed()
    {
        var ticket = await Table4();
        await _f.Db.Printers.Where(p => p.Name == "Ticket Printer 1").ExecuteUpdateAsync(u => u.SetProperty(p => p.Active, false));

        await FluentActions.Awaiting(() => Print(ticket.Id)).Should().ThrowAsync<DomainException>().WithMessage("*switched off*");

        var saved = _f.Ticket(ticket.Number);
        saved.BillNumber.Should().Be(0);
        saved.PrintRound.Should().Be(0);
        saved.Lines.Should().OnlyContain(l => l.Status == OrderLineStatus.New && l.PrintRound == null);
        Jobs(ticket.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task On_pillar_1_the_bill_goes_to_ticket_printer_1_and_the_slip_to_the_pillar()
    {
        await _f.LoginOn("Ruth", "Pillar 1");
        var ticket = await _f.Start("Pool 1", "Pineapple Punch");

        await Print(ticket.Id);

        Jobs(ticket.Id).Select(j => (j.Kind, j.Printer!.Name)).Should().Equal(
            (PrintJobKind.Bill, "Ticket Printer 1"), (PrintJobKind.Dispense, "Pillar 1"));
        Jobs(ticket.Id)[1].TextPreview.Should().Contain("COUNTER A", "Pillar 1 dispenses under the Counter A header");
    }

    [Fact]
    public async Task The_dispense_slip_lists_each_persons_lines()
    {
        var ticket = await _f.Start("4", "Pineapple Punch");
        await _f.Get<UnlockTicket>().Execute(ticket.Id, CancellationToken.None);
        await _f.LoginOn("Ruth", "Counter A");
        await _f.Get<LockTicket>().Execute(ticket.Id, CancellationToken.None);
        await _f.Add(ticket.Id, "Guiness", 1, ["Warm"]);

        await Print(ticket.Id);

        var slip = Jobs(ticket.Id)[1].TextPreview;
        slip.Should().Contain("By: edna").And.Contain("By: Ruth").And.Contain("WARM");
        slip.IndexOf("By: edna", StringComparison.Ordinal).Should().BeLessThan(slip.IndexOf("Pineapple", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reprint_waits_until_new_items_have_gone_to_the_bar()
    {
        var ticket = await Table4();
        await Print(ticket.Id);
        await _f.SetPermission("Waiter", PermissionKeys.TicketReprint, PermissionValue.Allowed);
        await _f.Add(ticket.Id, "Pineapple Punch");

        var reprint = () => Reprint(ticket.Id);

        (await reprint.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(PrintCodes.NewItemsFirst);
        Jobs(ticket.Id).Should().HaveCount(2, "no bill without the dispense slip for the new item");
        await Print(ticket.Id);
        Jobs(ticket.Id).Count(j => j.Kind == PrintJobKind.Dispense).Should().Be(2, "Print sends the new item to the bar with the bill");
    }

    [Fact]
    public async Task A_reprint_prints_only_the_bill_with_the_next_number_marked_reprint_and_is_audited()
    {
        var ticket = await Table4();
        await Print(ticket.Id);
        _f.Clock.Advance(TimeSpan.FromSeconds(2));
        await _f.SetPermission("Waiter", PermissionKeys.TicketReprint, PermissionValue.Allowed);

        var again = await Reprint(ticket.Id);

        again.BillNumber.Should().Be(2);
        var jobs = Jobs(ticket.Id);
        jobs.Should().HaveCount(3);
        jobs[2].Kind.Should().Be(PrintJobKind.Bill);
        jobs[2].TextPreview.Should().Contain("BILL 2 · Ticket #1001").And.Contain("REPRINT");
        var audit = _f.Read().AuditEntries.Single(a => a.Action == AuditActions.BillReprinted);
        audit.NewValueJson.Should().Contain("\"bill\":2").And.Contain("\"total\":");
    }

    [Fact]
    public async Task A_waiters_reprint_needs_a_supervisor_once_and_not_allowed_is_refused()
    {
        var ticket = await Table4();
        await Print(ticket.Id);

        await FluentActions.Awaiting(() => Reprint(ticket.Id)).Should().ThrowAsync<ApprovalRequiredException>();

        var token = await _f.ApprovalToken(PermissionKeys.TicketReprint);
        _f.Approval.ApprovalToken = token;
        (await Reprint(ticket.Id)).BillNumber.Should().Be(2);
        var sam = _f.User("SAM").Id;
        _f.Read().AuditEntries.Single(a => a.Action == AuditActions.BillReprinted).ApprovedByUserId.Should().Be(sam);

        _f.Approval.ApprovalToken = token;
        await FluentActions.Awaiting(() => Reprint(ticket.Id)).Should().ThrowAsync<ApprovalRequiredException>().WithMessage("*already used*");

        _f.Approval.ApprovalToken = await _f.ApprovalToken(PermissionKeys.TicketReprint, age: TimeSpan.FromSeconds(61));
        await FluentActions.Awaiting(() => Reprint(ticket.Id)).Should().ThrowAsync<ApprovalRequiredException>().WithMessage("*expired*");

        await _f.SetPermission("Waiter", PermissionKeys.TicketReprint, PermissionValue.NotAllowed);
        await FluentActions.Awaiting(() => Reprint(ticket.Id)).Should().ThrowAsync<PermissionDeniedException>();
        _f.Ticket(ticket.Number).BillNumber.Should().Be(2);
    }

    [Fact]
    public async Task In_send_and_bill_mode_send_prints_the_slip_and_print_bill_the_bill()
    {
        var settings = await _f.Db.BusinessSettings.SingleAsync();
        settings.PrintMode = PrintMode.SendAndBill;
        await _f.Db.SaveChangesAsync();
        var ticket = await Table4();
        var actions = _f.Get<PrintTicket>();

        var sent = await actions.Send(ticket.Id, CancellationToken.None);
        sent.BillNumber.Should().Be(0);
        sent.Lines.Should().OnlyContain(l => l.Status == "Printed");
        Jobs(ticket.Id).Select(j => j.Kind).Should().Equal(PrintJobKind.Dispense);

        var billed = await _f.Get<PrintTicket>().PrintBill(ticket.Id, CancellationToken.None);
        billed.BillNumber.Should().Be(1);
        Jobs(ticket.Id).Select(j => j.Kind).Should().Equal(PrintJobKind.Dispense, PrintJobKind.Bill);
        Jobs(ticket.Id)[1].TextPreview.Should().NotContain("REPRINT");

        // Nothing has changed since: the same bill again is a reprint (a waiter needs approval).
        await FluentActions.Awaiting(() => _f.Get<PrintTicket>().PrintBill(ticket.Id, CancellationToken.None))
            .Should().ThrowAsync<ApprovalRequiredException>();
    }

    [Fact]
    public async Task A_failed_job_can_be_retried_from_the_order_screen()
    {
        var ticket = await Table4();
        await Print(ticket.Id);
        var bill = Jobs(ticket.Id)[0];
        await _f.Db.PrintJobs.Where(j => j.Id == bill.Id).ExecuteUpdateAsync(u => u
            .SetProperty(j => j.Status, PrintJobStatus.Failed).SetProperty(j => j.LastError, "No answer"));

        (await _f.Get<GetTicket>().Execute(ticket.Id, CancellationToken.None)).FailedJobs.Should().ContainSingle()
            .Which.Should().Match<FailedJobDto>(j => j.Kind == "Bill" && j.PrinterName == "Ticket Printer 1");

        await _f.Get<RetryTicketPrint>().Execute(ticket.Id, bill.Id, CancellationToken.None);

        var retried = _f.Read().PrintJobs.Single(j => j.Id == bill.Id);
        retried.Status.Should().Be(PrintJobStatus.Queued);
        retried.CreatedAt.Should().Be(bill.CreatedAt, "it keeps its place before the dispense slip");
        await FluentActions.Awaiting(() => _f.Get<RetryTicketPrint>().Execute(Guid.NewGuid(), bill.Id, CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_takeaway_bill_names_the_customer()
    {
        var ticket = await _f.Get<StartTicket>().Execute(
            new StartTicketRequest(null, "Otieno", [new NewLineRequest(_f.Item("Pineapple Punch"), 1)]), CancellationToken.None);

        await Print(ticket.Id);

        Jobs(ticket.Id)[0].TextPreview.Should().Contain("Takeaway · Otieno");
    }
}
