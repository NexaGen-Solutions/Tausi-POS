using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nexagen.Application.Auth;
using Nexagen.Domain.People;
using Nexagen.Domain.Settings;
using Nexagen.Tests.Support;

namespace Nexagen.Tests.Auth;

public class SessionKeeperTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.Zero));
    private readonly SessionKeeper _sessions;
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _terminal = Guid.NewGuid();

    public SessionKeeperTests()
    {
        _db.Context.BusinessSettings.Add(new BusinessSettings { Name = "Test", OtherJson = "{}" });
        _db.Context.SaveChanges();
        _sessions = new SessionKeeper(_db.Context, _clock);
    }

    public void Dispose() => _db.Dispose();

    private async Task<UserSession> Start(Guid? terminal = null)
    {
        var session = await _sessions.Start(_user, terminal ?? _terminal, CancellationToken.None);
        await _db.Context.SaveChangesAsync();
        return session;
    }

    private Task<string?> Use(UserSession s, bool refresh = true) =>
        _sessions.Check(s.Id, _user, s.TerminalId, refresh, CancellationToken.None);

    [Fact]
    public async Task A_new_session_is_usable()
    {
        var session = await Start();
        (await Use(session)).Should().BeNull();
        session.ExpiresAt.Should().Be(_clock.GetUtcNow() + TimeSpan.FromHours(12));
    }

    [Fact]
    public async Task Ten_minutes_without_use_ends_it()
    {
        var session = await Start();
        _clock.Advance(TimeSpan.FromMinutes(10));

        (await Use(session)).Should().Be(SessionEndReasons.Idle);
        (await Use(session)).Should().Be(SessionEndReasons.Idle, "an ended session stays ended");
        (await _db.NewContext().UserSessions.SingleAsync()).EndReason.Should().Be(SessionEndReasons.Idle);
    }

    [Fact]
    public async Task A_request_just_before_the_timeout_refreshes_it()
    {
        var session = await Start();
        _clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        (await Use(session)).Should().BeNull();

        _clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        (await Use(session)).Should().BeNull("the last request reset the idle timer");
    }

    [Fact]
    public async Task Hub_checks_do_not_count_as_use()
    {
        var session = await Start();
        _clock.Advance(TimeSpan.FromMinutes(6));
        (await Use(session, refresh: false)).Should().BeNull();

        _clock.Advance(TimeSpan.FromMinutes(4));
        (await Use(session)).Should().Be(SessionEndReasons.Idle);
    }

    [Fact]
    public async Task The_idle_time_comes_from_settings()
    {
        var settings = await _db.Context.BusinessSettings.SingleAsync();
        settings.OtherJson = """{"pos":{"idleLogoutMinutes":1}}""";
        await _db.Context.SaveChangesAsync();
        var session = await Start();

        _clock.Advance(TimeSpan.FromSeconds(59));
        (await Use(session)).Should().BeNull();
        _clock.Advance(TimeSpan.FromSeconds(60));
        (await Use(session)).Should().Be(SessionEndReasons.Idle);
    }

    [Fact]
    public async Task Twelve_hours_is_the_most_even_when_busy()
    {
        var session = await Start();
        // A request every 8 minutes keeps it from going idle: 89 of them reach 11 h 52 min.
        for (var i = 0; i < 89; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(8));
            (await Use(session)).Should().BeNull();
        }
        _clock.Advance(TimeSpan.FromMinutes(8));
        (await Use(session)).Should().Be(SessionEndReasons.Expired);
    }

    [Fact]
    public async Task Logging_in_on_the_same_terminal_ends_the_previous_session()
    {
        var first = await Start();
        var other = await Start(Guid.NewGuid());
        var second = await Start();

        (await Use(first)).Should().Be(SessionEndReasons.Replaced);
        (await Use(second)).Should().BeNull();
        (await Use(other)).Should().BeNull("sessions on other terminals are not affected");
    }

    [Fact]
    public async Task A_session_only_works_on_its_terminal()
    {
        var session = await Start();
        (await _sessions.Check(session.Id, _user, Guid.NewGuid(), true, CancellationToken.None))
            .Should().Be(SessionEndReasons.Expired);
    }

    [Fact]
    public async Task Ending_it_is_final()
    {
        var session = await Start();
        await _sessions.End(session.Id, SessionEndReasons.Logout, CancellationToken.None);

        (await Use(session)).Should().Be(SessionEndReasons.Logout);
    }
}
