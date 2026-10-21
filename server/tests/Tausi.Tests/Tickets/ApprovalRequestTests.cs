using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Approvals;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Printing;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

public class ApprovalRequestTests : ApprovalsFixtureBase
{
    private async Task<SessionUser> Edna() => await F.LoginOn("edna", "Pillar 1");

    /// <summary>edna on Pillar 1 asks for a void of 1 printed Tusker, sent to MAU (logged in at Counter B).</summary>
    private async Task<(TicketDetailDto Ticket, ApprovalRequestDto Request, SessionUser Edna, SessionUser Mau)> VoidRequested()
    {
        var mau = await F.LoginOn("MAU", "Counter B");
        var edna = await F.LoginOn("edna", "Pillar 1");
        var ticket = await Printed("7", ("Tusker Lager", 2));
        var request = await Request(ApprovalKind.Void, ticket.Id, LineOf(ticket, "Tusker Lager"),
            new VoidLineRequest(1, "Wrong item"), mau.UserId);
        return (ticket, request, edna, mau);
    }

    [Fact]
    public async Task A_void_request_goes_to_approvers_shows_as_pending_and_runs_when_approved()
    {
        var (ticket, request, edna, mau) = await VoidRequested();

        request.Should().Match<ApprovalRequestDto>(r => r.Status == "Pending" && r.Summary == "Void 1 × Tusker Lager" && r.Amount == 250m
            && r.Reason == "Wrong item" && r.Where == "Table 7" && r.Terminal == "Pillar 1" && r.Mine && !r.CanDecide);
        F.Events.Published.Should().Contain(e => e.Name == PosEventNames.ApprovalRequested);
        var pendingLine = (await Detail(ticket.Id)).Lines.Single(l => l.Name == "Tusker Lager").Pending;
        pendingLine.Should().NotBeNull();
        pendingLine!.Kind.Should().Be("Void");
        pendingLine.Mine.Should().BeTrue();

        F.ActAs(mau);
        var waiting = await F.Get<ListApprovals>().Execute("pending", None);
        waiting.Single().Should().Match<ApprovalRequestDto>(r => r.SentToMe && r.CanDecide && r.RequestedBy == "edna");

        var approved = await Approve(request.Id);

        approved.Status.Should().Be("Approved");
        approved.DecidedBy.Should().Be("MAU");
        var lines = F.Ticket(ticket.Number).Lines;
        lines.Should().ContainSingle(l => l.Status == OrderLineStatus.Voided && l.Quantity == 1);
        lines.Should().ContainSingle(l => l.Status == OrderLineStatus.Printed && l.Quantity == 1);
        F.Read().VoidLogs.Single().Should().Match<VoidLog>(v => v.RequestedByUserId == edna.UserId && v.ApprovedByUserId == mau.UserId
            && v.TerminalId == edna.TerminalId && v.ApprovalRequestId == request.Id);
        // The corrected bill prints on the requester's terminal (Pillar 1 → Ticket Printer 1); the slip goes to the bar that poured it.
        var jobs = Jobs(ticket.Id);
        jobs.Last(j => j.Kind == PrintJobKind.Bill).TextPreview.Should().Contain("Corrected bill");
        jobs.Single(j => j.Kind == PrintJobKind.Void).TextPreview.Should().Contain("By: edna · Approved: MAU");
        F.Events.Published.Should().Contain(e => e.Name == PosEventNames.ApprovalResolved);
        (await F.Get<ListApprovals>().Execute("decided", None)).Single().Status.Should().Be("Approved");
    }

    [Fact]
    public async Task The_approver_list_has_only_logged_in_users_who_can_approve_that_kind()
    {
        await F.LoginOn("CYNTHIA", "Counter A");
        await F.LoginOn("CAROL", "Pillar 2");
        await F.LoginOn("MAU", "Counter B");
        await Edna();

        var voiders = await F.Get<Approvers>().Execute(ApprovalKind.Void, None);
        voiders.Should().ContainSingle().Which.Should().Be(new ApproverDto(F.User("MAU").Id, "MAU", "Supervisor", "Counter B"));

        (await F.Get<Approvers>().Execute(ApprovalKind.PriceChange, None)).Should().BeEmpty("only Administrator may, and isn't logged in");
        await F.LoginOn("Administrator", "Pillar 2");
        await Edna();
        (await F.Get<Approvers>().Execute(ApprovalKind.PriceChange, None)).Select(a => a.Name).Should().Equal("Administrator");

        // MAU's session goes idle: no longer listed.
        F.Clock.Advance(TimeSpan.FromMinutes(11));
        await Edna();
        (await F.Get<Approvers>().Execute(ApprovalKind.Void, None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Nobody_approves_their_own_request_and_only_allowed_users_approve()
    {
        // MAU needs approval for a price change; he can't approve it himself, by request or by PIN.
        var mau = await F.LoginOn("MAU", "Counter A");
        var ticket = await Printed("7", ("Tusker Lager", 1));
        var request = await Request(ApprovalKind.PriceChange, ticket.Id, LineOf(ticket, "Tusker Lager"),
            new ChangePriceRequest(1, 200m, "Happy hour"));

        await FluentActions.Awaiting(() => Approve(request.Id)).Should().ThrowAsync<ForbiddenException>().WithMessage("*own*");
        await FluentActions.Awaiting(() => F.Get<IssueApproval>().Execute(new IssueApprovalRequest("7007", PermissionKeys.OrderChangePrice), None))
            .Should().ThrowAsync<Exception>();

        // CAROL (cashier) can't approve a void.
        var (_, voidRequest, _, _) = await VoidRequested();
        await F.LoginOn("CAROL", "Counter A");
        await FluentActions.Awaiting(() => Approve(voidRequest.Id)).Should().ThrowAsync<PermissionDeniedException>();
        (await F.Get<ListApprovals>().Execute("pending", None)).Should().BeEmpty("CAROL can approve none of them");

        await F.LoginOn("Administrator", "Counter B");
        (await Approve(request.Id)).Status.Should().Be("Approved");
        F.Ticket(ticket.Number).Total.Should().Be(200m);
        _ = mau;
    }

    [Fact]
    public async Task Approving_twice_fails_the_second_time()
    {
        var (_, request, _, mau) = await VoidRequested();
        F.ActAs(mau);
        await Approve(request.Id);
        await F.LoginOn("Administrator", "Counter A");

        await FluentActions.Awaiting(() => Approve(request.Id)).Should().ThrowAsync<ConflictException>()
            .Where(e => e.Code == ApprovalCodes.AlreadyDecided);
        F.Read().VoidLogs.Should().HaveCount(1);
    }

    [Fact]
    public async Task Reject_and_cancel_change_nothing_on_the_ticket()
    {
        var (ticket, request, edna, mau) = await VoidRequested();
        F.ActAs(mau);
        var rejected = await Reject(request.Id, "It was poured already");
        rejected.Should().Match<ApprovalRequestDto>(r => r.Status == "Rejected" && r.DecisionNote == "It was poured already");

        F.ActAs(edna);
        var again = await Request(ApprovalKind.Void, ticket.Id, LineOf(ticket, "Tusker Lager"), new VoidLineRequest(2, "Customer changed mind"));
        F.ActAs(mau);
        await FluentActions.Awaiting(() => Cancel(again.Id)).Should().ThrowAsync<ForbiddenException>("only the requester cancels");
        F.ActAs(edna);
        (await Cancel(again.Id)).Status.Should().Be("Cancelled");

        F.Ticket(ticket.Number).Total.Should().Be(500m);
        F.Read().VoidLogs.Should().BeEmpty();
        Jobs(ticket.Id).Should().NotContain(j => j.Kind == PrintJobKind.Void);
        (await Detail(ticket.Id)).Lines.Should().OnlyContain(l => l.Pending == null);
    }

    [Fact]
    public async Task A_pending_request_blocks_other_changes_to_its_line_and_settling()
    {
        var (ticket, request, edna, mau) = await VoidRequested();
        var line = LineOf(ticket, "Tusker Lager");

        await FluentActions.Awaiting(() => Request(ApprovalKind.Void, ticket.Id, line, new VoidLineRequest(1, "Wrong item")))
            .Should().ThrowAsync<ConflictException>().Where(e => e.Code == ActionCodes.RequestPending);
        await FluentActions.Awaiting(() => SettleChecks.EnsureNoPendingRequests(F.Read(), ticket.Id, None))
            .Should().ThrowAsync<ConflictException>().Where(e => e.Code == ApprovalCodes.RequestsPending);

        F.ActAs(mau);
        await TakeLock(ticket.Id);
        await FluentActions.Awaiting(() => Void(ticket.Id, line)).Should().ThrowAsync<ConflictException>().WithMessage("*waiting for approval*");

        await Approve(request.Id);
        await SettleChecks.EnsureNoPendingRequests(F.Read(), ticket.Id, None);
        _ = edna;
    }

    [Fact]
    public async Task Requests_expire_after_the_setting_and_then_no_longer_block_settling()
    {
        var (ticket, request, _, mau) = await VoidRequested();
        F.Clock.Advance(TimeSpan.FromMinutes(29));
        (await F.Get<ExpireApprovals>().Run(None)).Should().Be(0);

        F.Clock.Advance(TimeSpan.FromMinutes(2));
        (await F.Get<ExpireApprovals>().Run(None)).Should().Be(1);

        var expired = F.Read().ApprovalRequests.Single();
        expired.Should().Match<ApprovalRequest>(r => r.Status == ApprovalStatus.Expired && r.DecisionNote == ApprovalExpiry.TimedOut);
        await SettleChecks.EnsureNoPendingRequests(F.Read(), ticket.Id, None);
        F.ActAs(mau);
        await FluentActions.Awaiting(() => Approve(request.Id)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task The_expiry_minutes_come_from_the_setting()
    {
        var settings = F.Db.BusinessSettings.Single();
        settings.OtherJson = """{"pos":{"approvalExpiryMinutes":5}}""";
        await F.Db.SaveChangesAsync();
        F.Db.ChangeTracker.Clear();
        await VoidRequested();

        F.Clock.Advance(TimeSpan.FromMinutes(5));
        (await F.Get<ExpireApprovals>().Run(None)).Should().Be(1);
    }

    [Fact]
    public async Task Merging_the_ticket_away_expires_its_requests()
    {
        var mau = await F.LoginOn("MAU", "Counter B");
        await Edna();
        var first = await F.NewTicket("7", ("Tusker Lager", 1));
        F.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await F.Get<PrintTicket>().Print((await F.NewTicket("7", ("Guiness", 1))).Id, None);
        var request = await Request(ApprovalKind.Void, second.Id, LineOf(second, "Guiness"), new VoidLineRequest(1, "Wrong item"));

        F.ActAs(mau);
        F.Clock.Advance(Ticket.LockLifetime + TimeSpan.FromSeconds(1));
        await F.Get<MergeTickets>().Execute(new MergeTicketsRequest([second.Id], first.Id), None);

        F.Read().ApprovalRequests.Single(r => r.Id == request.Id).Should().Match<ApprovalRequest>(r =>
            r.Status == ApprovalStatus.Expired && r.DecisionNote == ApprovalExpiry.TicketMerged);
    }

    [Fact]
    public async Task Approving_checks_again_and_expires_a_request_that_no_longer_fits()
    {
        var (ticket, request, _, mau) = await VoidRequested();
        // The line went meanwhile (e.g. voided another way).
        await F.Db.OrderLines.Where(l => l.TicketId == ticket.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, OrderLineStatus.Voided).SetProperty(l => l.VoidedAt, F.Clock.GetUtcNow()));
        F.ActAs(mau);

        await FluentActions.Awaiting(() => Approve(request.Id)).Should().ThrowAsync<ConflictException>()
            .Where(e => e.Code == ApprovalCodes.Expired && e.Message.Contains("already taken off"));

        F.Read().ApprovalRequests.Single().Status.Should().Be(ApprovalStatus.Expired);
        F.Read().VoidLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_discount_request_is_checked_again_when_approved()
    {
        var cashier = await F.LoginOn("CAROL", "Counter A");
        var ticket = await Printed("7", ("Tusker Lager", 2));
        var request = await Request(ApprovalKind.Discount, ticket.Id, null, new DiscountRequest(DiscountKind.Amount, 400m, "Promotion"));
        (await Detail(ticket.Id)).Pending.Should().ContainSingle(p => p.Kind == "Discount");

        // A line is voided meanwhile: 400 is now more than the total of 250.
        await F.LoginOn("MAU", "Counter B");
        await TakeLock(ticket.Id);
        await Void(ticket.Id, LineOf(ticket, "Tusker Lager"), 1);

        await FluentActions.Awaiting(() => Approve(request.Id)).Should().ThrowAsync<ConflictException>().Where(e => e.Code == ApprovalCodes.Expired);
        _ = cashier;
    }

    [Fact]
    public async Task Gift_discount_and_price_change_requests_run_when_approved()
    {
        var carol = await F.LoginOn("CAROL", "Counter A");
        var ticket = await Printed("7", ("Guiness", 2), ("Tusker Lager", 2));
        var gift = await Request(ApprovalKind.Gift, ticket.Id, LineOf(ticket, "Guiness"), new GiftLineRequest(1, "House"));
        var discount = await Request(ApprovalKind.Discount, ticket.Id, null, new DiscountRequest(DiscountKind.Percent, 10m, "Regular customer"));
        var mau = await F.LoginOn("MAU", "Counter B");
        await Approve(gift.Id);
        await Approve(discount.Id);
        var price = await F.Get<CreateApprovalRequest>().Execute(new CreateApprovalRequestDto(ApprovalKind.PriceChange, ticket.Id,
            LineOf(ticket, "Tusker Lager"), System.Text.Json.JsonSerializer.SerializeToElement(new { quantity = 2, newUnitPrice = 200, reason = "Happy hour" })), None);
        await F.LoginOn("Administrator", "Pillar 2");
        await Approve(price.Id);

        var t = F.Ticket(ticket.Number);
        t.Lines.Single(l => l.IsGift).Quantity.Should().Be(1);
        t.Subtotal.Should().Be(700m);
        t.DiscountTotal.Should().Be(70m);
        t.Total.Should().Be(630m);
        F.Read().GiftLogs.Single().RequestedByUserId.Should().Be(carol.UserId);
        F.Read().DiscountLogs.Single().ApprovedByUserId.Should().Be(mau.UserId);
        F.Read().PriceChangeLogs.Single(p => p.Kind == Domain.Menu.PriceChangeKind.Ticket).ChangedByUserId.Should().Be(mau.UserId);
    }

    [Fact]
    public async Task A_reprint_request_prints_the_bill_on_the_requesters_terminal()
    {
        var mau = await F.LoginOn("MAU", "Counter B");
        await Edna();
        var ticket = await Printed("7", ("Tusker Lager", 1));
        var request = await Request(ApprovalKind.Reprint, ticket.Id, null, new ReprintRequest("Customer lost the first bill"));
        request.Reason.Should().Be("Customer lost the first bill");

        F.ActAs(mau);
        Later();
        await Approve(request.Id);

        var bill = Jobs(ticket.Id).Last(j => j.Kind == PrintJobKind.Bill);
        bill.Printer!.Name.Should().Be("Ticket Printer 1", "edna's terminal, Pillar 1, prints receipts at Counter A, not MAU's Counter B");
        bill.TextPreview.Should().Contain("REPRINT").And.Contain("BILL 2");
    }

    [Fact]
    public async Task Move_and_merge_requests_run_when_approved()
    {
        var mau = await F.LoginOn("MAU", "Counter B");
        var edna = await Edna();
        await F.SetPermission("Waiter", PermissionKeys.TicketMove, PermissionValue.NeedsApproval);
        await F.SetPermission("Waiter", PermissionKeys.TicketMerge, PermissionValue.NeedsApproval);
        var first = await F.NewTicket("7", ("Tusker Lager", 1));
        F.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await F.NewTicket("7", ("Guiness", 1));
        var lone = await F.NewTicket("9", ("Guiness", 1));
        // Off the order screen, the locks are given back.
        F.Clock.Advance(Ticket.LockLifetime + TimeSpan.FromSeconds(1));

        var move = await Request(ApprovalKind.Move, lone.Id, null, new MoveTicketRequest(F.Table("12").Id));
        move.Summary.Should().Be("Move to Table 12");
        var merge = await Request(ApprovalKind.Merge, first.Id, null, new MergeTicketsRequest([second.Id], first.Id));
        merge.Summary.Should().Be($"Merge #{second.Number} into #{first.Number}");

        F.ActAs(mau);
        await Approve(move.Id);
        await Approve(merge.Id);

        F.Ticket(lone.Number).TableId.Should().Be(F.Table("12").Id);
        F.Ticket(second.Number).Status.Should().Be(TicketStatus.Merged);
        F.Ticket(first.Number).Lines.Should().HaveCount(2);
        F.Read().AuditEntries.Where(a => a.Action == Application.Auditing.AuditActions.TicketMoved).Single()
            .Should().Match<Domain.Audit.AuditEntry>(a => a.UserId == edna.UserId && a.ApprovedByUserId == mau.UserId);
    }

    [Fact]
    public async Task Allowed_users_do_it_themselves_and_not_allowed_users_cant_ask()
    {
        await F.LoginOn("MAU", "Counter A");
        var ticket = await Printed("7", ("Tusker Lager", 1));
        await FluentActions.Awaiting(() => Request(ApprovalKind.Void, ticket.Id, LineOf(ticket, "Tusker Lager"), new VoidLineRequest(1, "Wrong item")))
            .Should().ThrowAsync<ConflictException>().Where(e => e.Code == ApprovalCodes.NotNeeded);

        await Edna();
        await FluentActions.Awaiting(() => Request(ApprovalKind.Gift, ticket.Id, LineOf(ticket, "Tusker Lager"), new GiftLineRequest(1, "House")))
            .Should().ThrowAsync<PermissionDeniedException>();
    }
}
