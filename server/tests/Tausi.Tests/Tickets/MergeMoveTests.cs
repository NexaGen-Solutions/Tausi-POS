using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.People;
using Tausi.Domain.Service;

namespace Tausi.Tests.Tickets;

public class MergeMoveTests : IAsyncLifetime
{
    private TicketsFixture _f = null!;
    public async Task InitializeAsync() => _f = await TicketsFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<TicketSummaryDto> Merge(Guid into, params Guid[] others) =>
        _f.Get<MergeTickets>().Execute(new MergeTicketsRequest(others, into), CancellationToken.None);

    private Task<TicketSummaryDto> Move(Guid id, string table) =>
        _f.Get<MoveTicket>().Execute(id, new MoveTicketRequest(_f.Table(table).Id), CancellationToken.None);

    /// <summary>Two tickets on Table 7: the first by edna on Counter A, the second by Ruth on Pillar 1. Neither is locked.</summary>
    private async Task<(TicketDetailDto First, TicketDetailDto Second)> TwoOnTable7()
    {
        var first = await _f.NewTicket("7", ("Tusker Lager", 2));
        await _f.Get<UnlockTicket>().Execute(first.Id, CancellationToken.None);
        _f.Clock.Advance(TimeSpan.FromMinutes(5));
        await _f.LoginOn("Ruth", "Pillar 1");
        var second = await _f.NewTicket("7", ("Guiness", 1), ("Sodas 300ml", 2));
        await _f.Get<UnlockTicket>().Execute(second.Id, CancellationToken.None);
        return (first, second);
    }

    [Fact]
    public async Task Merging_keeps_the_oldest_ticket_and_every_line_keeps_its_waiter_and_terminal()
    {
        var (first, second) = await TwoOnTable7();
        await _f.LoginOn("edna", "Counter A");
        _f.Events.Published.Clear();

        var kept = await Merge(first.Id, second.Id);

        kept.Number.Should().Be(first.Number);
        kept.Total.Should().Be(500m + 300m + 200m);
        var into = _f.Ticket(first.Number);
        into.Lines.Should().HaveCount(3);
        var ruthsLines = into.Lines.Where(l => l.Name != "Tusker Lager").ToList();
        ruthsLines.Should().OnlyContain(l => l.AddedByUserId == _f.User("Ruth").Id);
        ruthsLines.Should().OnlyContain(l => l.AddedTerminalId == _f.Read().Terminals.Single(t => t.Name == "Pillar 1").Id);
        var merged = _f.Ticket(second.Number);
        merged.Status.Should().Be(TicketStatus.Merged);
        merged.MergedIntoTicketId.Should().Be(first.Id);
        merged.Lines.Should().BeEmpty();
        _f.Events.Published.Count(e => e.Name == PosEventNames.TicketChanged).Should().Be(2);

        var audit = _f.Read().AuditEntries.Single(a => a.Action == AuditActions.TicketMerged);
        audit.UserId.Should().Be(_f.User("edna").Id);
        audit.OldValueJson.Should().Contain($"{first.Number}").And.Contain($"{second.Number}");
        audit.NewValueJson.Should().Contain("\"total\":1000");
    }

    [Fact]
    public async Task Only_the_oldest_ticket_can_be_kept()
    {
        var (first, second) = await TwoOnTable7();

        var act = () => Merge(second.Id, first.Id);

        await act.Should().ThrowAsync<DomainException>().WithMessage($"The oldest ticket is kept: merge into #{first.Number}.");
    }

    [Fact]
    public async Task Merging_across_tables_or_with_a_ticket_that_is_not_open_is_refused()
    {
        var (first, second) = await TwoOnTable7();
        var other = await _f.NewTicket("8");
        await _f.Get<UnlockTicket>().Execute(other.Id, CancellationToken.None);

        await FluentActions.Awaiting(() => Merge(first.Id, other.Id)).Should().ThrowAsync<DomainException>()
            .WithMessage("Only tickets on the same table can be merged.");

        await _f.Db.Tickets.Where(k => k.Id == second.Id).ExecuteUpdateAsync(u => u.SetProperty(k => k.Status, TicketStatus.Settled));
        await FluentActions.Awaiting(() => Merge(first.Id, second.Id)).Should().ThrowAsync<DomainException>()
            .WithMessage($"#{second.Number} is not open.");
    }

    [Fact]
    public async Task Merging_is_refused_while_a_ticket_is_open_on_another_terminal()
    {
        var (first, second) = await TwoOnTable7();
        (await _f.Get<LockTicket>().Execute(second.Id, CancellationToken.None)).Editable.Should().BeTrue(); // Ruth on Pillar 1
        await _f.LoginOn("edna", "Counter A");

        var act = () => Merge(first.Id, second.Id);

        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be(TicketCodes.Locked);
        _f.Ticket(second.Number).Status.Should().Be(TicketStatus.Open);
    }

    [Fact]
    public async Task Merging_on_the_terminal_that_holds_the_lock_is_fine()
    {
        var (first, second) = await TwoOnTable7();
        (await _f.Get<LockTicket>().Execute(first.Id, CancellationToken.None)).Editable.Should().BeTrue(); // Ruth on Pillar 1

        (await Merge(first.Id, second.Id)).Number.Should().Be(first.Number);
    }

    [Fact]
    public async Task Moving_to_a_free_table_changes_only_the_table_and_is_audited()
    {
        var ticket = await _f.NewTicket("7", ("Tusker Lager", 1));
        _f.Clock.Advance(TimeSpan.FromMinutes(3));
        _f.Events.Published.Clear();

        await Move(ticket.Id, "4");

        var moved = _f.Ticket(ticket.Number);
        moved.TableId.Should().Be(_f.Table("4").Id);
        moved.Lines.Should().ContainSingle();
        moved.OpenedByUserId.Should().Be(_f.User("edna").Id);
        moved.LastActivityAt.Should().Be(_f.Clock.GetUtcNow());
        _f.Events.Published.Where(e => e.Name == PosEventNames.TicketChanged).Should().HaveCount(2, "one for Table 7, one for Table 4");
        var audit = _f.Read().AuditEntries.Single(a => a.Action == AuditActions.TicketMoved);
        audit.OldValueJson.Should().Contain("\"table\":\"7\"");
        audit.NewValueJson.Should().Contain("\"table\":\"4\"");
    }

    [Fact]
    public async Task Moving_onto_an_occupied_table_leaves_two_tickets_there()
    {
        var there = await _f.NewTicket("4");
        var ticket = await _f.NewTicket("7");

        await Move(ticket.Id, "4");

        _f.Read().Tickets.Where(k => k.TableId == _f.Table("4").Id && k.Status == TicketStatus.Open)
            .Select(k => k.Number).Should().BeEquivalentTo([there.Number, ticket.Number]);
    }

    [Fact]
    public async Task Moving_onto_an_inactive_table_or_a_ticket_open_elsewhere_is_refused()
    {
        var ticket = await _f.NewTicket("7");
        await _f.Db.Tables.Where(t => t.Name == "16").ExecuteUpdateAsync(u => u.SetProperty(t => t.Active, false));
        await FluentActions.Awaiting(() => Move(ticket.Id, "16")).Should().ThrowAsync<DomainException>()
            .WithMessage("That table is not in use.");

        await _f.LoginOn("Ruth", "Pillar 1");
        var act = () => Move(ticket.Id, "4");
        (await act.Should().ThrowAsync<ConflictException>()).Which.Message
            .Should().Be($"#{ticket.Number} is open on Counter A by edna. Wait until they leave it, then try again.");
    }

    [Theory]
    [InlineData(PermissionKeys.TicketMove)]
    [InlineData(PermissionKeys.TicketMerge)]
    public async Task Not_allowed_is_refused(string permission)
    {
        var (first, second) = await TwoOnTable7();
        await _f.SetPermission("Waiter", permission, PermissionValue.NotAllowed);

        var act = () => permission == PermissionKeys.TicketMove ? Move(first.Id, "4") : Merge(first.Id, second.Id);

        await act.Should().ThrowAsync<PermissionDeniedException>();
        _f.Ticket(first.Number).TableId.Should().Be(_f.Table("7").Id);
    }

    [Theory]
    [InlineData(PermissionKeys.TicketMove)]
    [InlineData(PermissionKeys.TicketMerge)]
    public async Task Needs_approval_works_with_a_supervisors_token_once(string permission)
    {
        var (first, second) = await TwoOnTable7();
        await _f.SetPermission("Waiter", permission, PermissionValue.NeedsApproval);
        Func<Task> act = () => permission == PermissionKeys.TicketMove ? Move(first.Id, "4") : Merge(first.Id, second.Id);

        await act.Should().ThrowAsync<ApprovalRequiredException>();

        var token = await _f.ApprovalToken(permission);
        _f.Approval.ApprovalToken = token;
        await act.Should().NotThrowAsync();
        var mau = _f.User("MAU").Id;
        _f.Read().AuditEntries.Should().Contain(a => a.ApprovedByUserId == mau);

        _f.Read().ApprovalGrants.Single().UsedAt.Should().NotBeNull("a token works once");
    }

    [Fact]
    public async Task A_used_approval_is_refused()
    {
        var (first, _) = await TwoOnTable7();
        await _f.SetPermission("Waiter", PermissionKeys.TicketMove, PermissionValue.NeedsApproval);
        var token = await _f.ApprovalToken(PermissionKeys.TicketMove);
        _f.Approval.ApprovalToken = token;
        await Move(first.Id, "4");

        _f.Approval.ApprovalToken = token;
        await FluentActions.Awaiting(() => Move(first.Id, "7")).Should().ThrowAsync<ApprovalRequiredException>()
            .WithMessage("*already used*");
    }

    [Fact]
    public async Task An_expired_approval_is_refused()
    {
        var (first, _) = await TwoOnTable7();
        await _f.SetPermission("Waiter", PermissionKeys.TicketMove, PermissionValue.NeedsApproval);
        _f.Approval.ApprovalToken = await _f.ApprovalToken(PermissionKeys.TicketMove, age: TimeSpan.FromSeconds(61));

        await FluentActions.Awaiting(() => Move(first.Id, "4")).Should().ThrowAsync<ApprovalRequiredException>()
            .WithMessage("*expired*");
    }
}
