using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nexagen.Application.Auditing;
using Nexagen.Application.Common;
using Nexagen.Application.Day;
using Nexagen.Application.Events;
using Nexagen.Application.Security;
using Nexagen.Domain.People;
using Nexagen.Domain.Service;
using Nexagen.Tests.Support;

namespace Nexagen.Tests.Day;

public class OpenDayTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 5, 14, 0, TimeSpan.Zero));
    private readonly FakeSession _session = new();
    private readonly RecordingEvents _events = new();
    private SessionUser _supervisor = null!;
    private SessionUser _waiter = null!;

    public async Task InitializeAsync()
    {
        var hasher = new FastPinHasher();
        var roles = await Builders.SeedRoles(_db.Context);
        var terminal = await Builders.AddTerminal(_db.Context);
        _supervisor = Builders.Session(await Builders.AddUser(_db.Context, hasher, "MAU", roles["Supervisor"], "3871"), roles["Supervisor"], terminal);
        _waiter = Builders.Session(await Builders.AddUser(_db.Context, hasher, "edna", roles["Waiter"], "4821"), roles["Waiter"], terminal);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private Task<WorkPeriodDto> Open(SessionUser user)
    {
        _session.User = user;
        var open = new OpenDay(
            _db.Context, _session, new PermissionService(_db.Context, _clock), new FakeApprovalSource(),
            new AuditLog(_db.Context, _session, _clock), _events, _clock);
        return open.Execute(CancellationToken.None);
    }

    [Fact]
    public async Task A_user_with_day_open_close_opens_the_day()
    {
        var period = await Open(_supervisor);

        var stored = await _db.NewContext().WorkPeriods.SingleAsync();
        stored.IsOpen.Should().BeTrue();
        stored.OpenedByUserId.Should().Be(_supervisor.UserId);
        stored.OpenedTerminalId.Should().Be(_supervisor.TerminalId);
        stored.OpenedAt.Should().Be(_clock.GetUtcNow());
        period.Id.Should().Be(stored.Id);
        (await _db.NewContext().AuditEntries.CountAsync(a => a.Action == AuditActions.DayOpened)).Should().Be(1);
    }

    [Fact]
    public async Task It_announces_day_opened()
    {
        await Open(_supervisor);
        _events.Published.Should().ContainSingle(e => e.Name == PosEventNames.DayOpened);
    }

    [Fact]
    public async Task A_waiter_may_not()
    {
        await FluentActions.Awaiting(() => Open(_waiter)).Should().ThrowAsync<PermissionDeniedException>();
        (await _db.NewContext().WorkPeriods.CountAsync()).Should().Be(0);
        _events.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_open_day_is_refused()
    {
        await Open(_supervisor);
        await FluentActions.Awaiting(() => Open(_supervisor)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task The_database_allows_only_one_open_day()
    {
        _db.Context.WorkPeriods.Add(new WorkPeriod { OpenedAt = _clock.GetUtcNow(), OpenedByUserId = _supervisor.UserId });
        _db.Context.WorkPeriods.Add(new WorkPeriod { OpenedAt = _clock.GetUtcNow(), OpenedByUserId = _supervisor.UserId });

        await FluentActions.Awaiting(() => _db.Context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Closed_days_do_not_count()
    {
        _db.Context.WorkPeriods.Add(new WorkPeriod
        {
            OpenedAt = _clock.GetUtcNow().AddDays(-1),
            OpenedByUserId = _supervisor.UserId,
            ClosedAt = _clock.GetUtcNow().AddHours(-8),
            IsOpen = false,
        });
        await _db.Context.SaveChangesAsync();

        await Open(_supervisor);
        (await _db.NewContext().WorkPeriods.CountAsync()).Should().Be(2);
    }
}
