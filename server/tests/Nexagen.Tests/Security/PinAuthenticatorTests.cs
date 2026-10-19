using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nexagen.Application.Auditing;
using Nexagen.Application.Auth;
using Nexagen.Domain.Devices;
using Nexagen.Domain.People;
using Nexagen.Tests.Support;

namespace Nexagen.Tests.Security;

public class PinAuthenticatorTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 19, 0, 0, TimeSpan.Zero));
    private readonly FastPinHasher _hasher = new();
    private PinAuthenticator _pins = null!;
    private Terminal _counterA = null!;
    private User _edna = null!;

    public async Task InitializeAsync()
    {
        var roles = await Builders.SeedRoles(_db.Context);
        _edna = await Builders.AddUser(_db.Context, _hasher, "edna", roles["Waiter"], "4821");
        await Builders.AddUser(_db.Context, _hasher, "gone", roles["Waiter"], "9999", active: false);
        _counterA = await Builders.AddTerminal(_db.Context);
        var session = new FakeSession();
        _pins = new PinAuthenticator(_db.Context, _hasher, new AuditLog(_db.Context, session, _clock), _clock);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private Task<User> Try(string pin) => _pins.Authenticate(_counterA.Id, pin, CancellationToken.None);

    [Fact]
    public async Task Right_pin_finds_the_user()
    {
        (await Try("4821")).Id.Should().Be(_edna.Id);
    }

    [Fact]
    public async Task Inactive_user_cannot_log_in()
    {
        await FluentActions.Awaiting(() => Try("9999")).Should().ThrowAsync<LoginRejectedException>();
    }

    [Fact]
    public async Task Wrong_pin_is_rejected_and_audited_with_attempts_left()
    {
        var ex = await FluentActions.Awaiting(() => Try("0000")).Should().ThrowAsync<LoginRejectedException>();
        ex.Which.AttemptsLeft.Should().Be(4);

        var audit = await _db.NewContext().AuditEntries.SingleAsync();
        audit.Action.Should().Be(AuditActions.PinLoginFailed);
        audit.TerminalId.Should().Be(_counterA.Id);
    }

    [Fact]
    public async Task Five_wrong_pins_in_two_minutes_lock_the_terminal_for_60_seconds()
    {
        for (var i = 0; i < 4; i++)
        {
            await FluentActions.Awaiting(() => Try("0000")).Should().ThrowAsync<LoginRejectedException>();
            _clock.Advance(TimeSpan.FromSeconds(20));
        }
        await FluentActions.Awaiting(() => Try("0000")).Should().ThrowAsync<LoginLockedException>();

        // Even the right PIN is refused while locked.
        _clock.Advance(TimeSpan.FromSeconds(59));
        await FluentActions.Awaiting(() => Try("4821")).Should().ThrowAsync<LoginLockedException>();

        _clock.Advance(TimeSpan.FromSeconds(2));
        (await Try("4821")).Id.Should().Be(_edna.Id);

        var actions = await _db.NewContext().AuditEntries.Select(a => a.Action).ToListAsync();
        actions.Should().ContainSingle(a => a == AuditActions.TerminalLocked);
    }

    [Fact]
    public async Task Wrong_pins_spread_over_more_than_two_minutes_do_not_lock()
    {
        for (var i = 0; i < 6; i++)
        {
            await FluentActions.Awaiting(() => Try("0000")).Should().ThrowAsync<LoginRejectedException>();
            _clock.Advance(TimeSpan.FromSeconds(31));
        }
    }

    [Fact]
    public async Task Lock_is_per_terminal()
    {
        for (var i = 0; i < 4; i++)
            await FluentActions.Awaiting(() => Try("0000")).Should().ThrowAsync<LoginRejectedException>();
        await FluentActions.Awaiting(() => Try("0000")).Should().ThrowAsync<LoginLockedException>();

        var counterB = await Builders.AddTerminal(_db.Context, "Counter B");
        (await _pins.Authenticate(counterB.Id, "4821", CancellationToken.None)).Id.Should().Be(_edna.Id);
    }

    [Fact]
    public async Task Pins_are_unique_among_active_users_in_the_database()
    {
        var waiter = await _db.Context.Roles.SingleAsync(r => r.Name == "Waiter");
        var act = () => Builders.AddUser(_db.Context, _hasher, "copy", waiter, "4821");
        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
