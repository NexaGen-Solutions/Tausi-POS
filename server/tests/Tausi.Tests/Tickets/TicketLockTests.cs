using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auth;
using Tausi.Application.Events;
using Tausi.Application.Tickets;

namespace Tausi.Tests.Tickets;

public class TicketLockTests : IAsyncLifetime
{
    private TicketsFixture _f = null!;
    public async Task InitializeAsync() => _f = await TicketsFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<TicketLockState> Lock(Guid id) => _f.Get<LockTicket>().Execute(id, CancellationToken.None);

    [Fact]
    public async Task The_terminal_that_saves_a_ticket_holds_its_lock_and_renews_it()
    {
        var ticket = await _f.NewTicket("4");
        _f.Ticket(ticket.Number).LockExpiresAt.Should().Be(_f.Clock.GetUtcNow() + TimeSpan.FromSeconds(90));

        _f.Clock.Advance(TimeSpan.FromSeconds(30));
        (await Lock(ticket.Id)).Editable.Should().BeTrue();

        _f.Ticket(ticket.Number).LockExpiresAt.Should().Be(_f.Clock.GetUtcNow() + TimeSpan.FromSeconds(90), "renewed for another 90 seconds");
        _f.Ticket(ticket.Number).RowVersion.Should().Be(0, "a lock is not an edit");
    }

    [Fact]
    public async Task A_second_terminal_gets_the_ticket_read_only_until_the_first_leaves()
    {
        var ticket = await _f.NewTicket("4");
        var edna = _f.Session.User!;
        await _f.LoginOn("Ruth", "Pillar 1");

        var second = await Lock(ticket.Id);
        second.Editable.Should().BeFalse();
        second.Message.Should().Be("Open on Counter A by edna. You can view it but not change it.");

        var ruth = _f.Session.User!;
        _f.ActAs(edna);
        _f.Events.Published.Clear();
        await _f.Get<UnlockTicket>().Execute(ticket.Id, CancellationToken.None);
        _f.Events.Published.Should().ContainSingle(e => e.Name == PosEventNames.TicketLock);

        _f.ActAs(ruth);
        (await Lock(ticket.Id)).Editable.Should().BeTrue();
    }

    [Fact]
    public async Task A_lock_not_renewed_for_90_seconds_expires()
    {
        var ticket = await _f.NewTicket("4");
        await _f.LoginOn("Ruth", "Pillar 1");

        _f.Clock.Advance(TimeSpan.FromSeconds(89));
        (await Lock(ticket.Id)).Editable.Should().BeFalse();

        _f.Clock.Advance(TimeSpan.FromSeconds(2));
        (await Lock(ticket.Id)).Editable.Should().BeTrue();
    }

    [Fact]
    public async Task Logging_out_releases_the_lock_at_once()
    {
        var ticket = await _f.NewTicket("4");
        _f.Events.Published.Clear();

        await _f.Get<Logout>().Execute(CancellationToken.None);

        _f.Ticket(ticket.Number).LockSessionId.Should().BeNull();
        _f.Events.Published.Should().Contain(e => e.Name == PosEventNames.TicketLock);
        await _f.LoginOn("Ruth", "Pillar 1");
        (await Lock(ticket.Id)).Editable.Should().BeTrue();
    }

    [Fact]
    public async Task An_idle_logout_releases_the_lock_on_the_next_sweep()
    {
        var ticket = await _f.NewTicket("4");
        var sweep = () => _f.Get<ReleaseTicketLocks>().Stale(CancellationToken.None);

        (await sweep()).Should().Be(0);
        // Renewals keep the lock alive, but the person stops using the terminal: after 10 minutes
        // (pos.idleLogoutMinutes) their session is over, and so is the lock.
        _f.Ticket(ticket.Number).LockSessionId.Should().NotBeNull();
        await _f.Db.Tickets.Where(k => k.Id == ticket.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.LockExpiresAt, _f.Clock.GetUtcNow().AddMinutes(30)));
        _f.Clock.Advance(TimeSpan.FromMinutes(11));

        (await sweep()).Should().Be(1);
        _f.Ticket(ticket.Number).LockSessionId.Should().BeNull();
    }

    [Fact]
    public async Task The_sweep_also_ends_locks_that_ran_out()
    {
        var ticket = await _f.NewTicket("4");
        _f.Clock.Advance(TimeSpan.FromSeconds(91));

        (await _f.Get<ReleaseTicketLocks>().Stale(CancellationToken.None)).Should().Be(1);
        _f.Ticket(ticket.Number).LockTerminalId.Should().BeNull();
    }
}
