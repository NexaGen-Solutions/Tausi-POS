using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Application.Events;
using Tausi.Application.Security;
using Tausi.Application.Tables;
using Tausi.Application.Tickets;
using Tausi.Domain.Common;
using Tausi.Domain.Service;
using Tausi.Tests.Support;

namespace Tausi.Tests.Tickets;

public class TablesAdminTests : IAsyncLifetime
{
    private TicketsFixture _f = null!;
    public async Task InitializeAsync() => _f = await TicketsFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private void AsAdmin()
    {
        var admin = _f.User("Administrator");
        _f.Session.User = Builders.Session(admin, _f.Roles["Admin"]);
        _f.Session.Terminal = null;
    }

    private Guid Area(string name) => _f.Read().Areas.Single(a => a.Name == name).Id;

    [Fact]
    public async Task Quick_add_creates_the_range_and_skips_names_that_exist()
    {
        AsAdmin();
        _f.Events.Published.Clear();

        var result = await _f.Get<QuickAddTables>().Execute(new QuickAddRequest(14, 20, Area("Main"), null), CancellationToken.None);

        result.Added.Should().Equal("17", "18", "19", "20");
        result.Skipped.Should().Equal("14", "15", "16");
        _f.Read().Tables.Single(t => t.Name == "17").SortOrder.Should().Be(17, "after the area's last table");
        _f.Events.Published.Should().ContainSingle(e => e.Name == PosEventNames.TablesChanged);
        _f.Read().AuditEntries.Should().Contain(a => a.Action == AuditActions.TablesAdded);
    }

    [Fact]
    public async Task Quick_add_takes_a_prefix_for_names_like_pool_3()
    {
        AsAdmin();

        var result = await _f.Get<QuickAddTables>().Execute(new QuickAddRequest(2, 4, Area("Pool"), "Pool"), CancellationToken.None);

        result.Added.Should().Equal("Pool 3", "Pool 4");
        result.Skipped.Should().Equal("Pool 2");
    }

    [Fact]
    public async Task A_table_with_open_tickets_cannot_be_switched_off_and_says_which()
    {
        var ticket = await _f.NewTicket("7");
        AsAdmin();
        var seven = _f.Table("7");

        var act = () => _f.Get<SaveTable>().Execute(seven.Id, new TableRequest("7", seven.AreaId, seven.SortOrder, false, seven.RowVersion), CancellationToken.None);

        var error = (await act.Should().ThrowAsync<DomainException>()).Which;
        error.Code.Should().Be(TicketCodes.TableInUse);
        error.Message.Should().Contain($"7 (#{ticket.Number})");
        _f.Table("7").Active.Should().BeTrue();
    }

    [Fact]
    public async Task An_area_with_open_tickets_on_its_tables_cannot_be_switched_off()
    {
        await _f.NewTicket("Pool 1");
        AsAdmin();
        var pool = _f.Read().Areas.Single(a => a.Name == "Pool");

        var act = () => _f.Get<SaveArea>().Execute(pool.Id, new AreaRequest("Pool", pool.SortOrder, false, pool.RowVersion), CancellationToken.None);

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(TicketCodes.TableInUse);
    }

    [Fact]
    public async Task A_table_with_history_can_be_switched_off_but_not_deleted()
    {
        var ticket = await _f.NewTicket("7");
        await _f.Db.Tickets.Where(k => k.Id == ticket.Id).ExecuteUpdateAsync(u => u.SetProperty(k => k.Status, TicketStatus.Settled));
        AsAdmin();
        var seven = _f.Table("7");

        await FluentActions.Awaiting(() => _f.Get<DeleteTableOrArea>().Table(seven.Id, CancellationToken.None))
            .Should().ThrowAsync<DomainException>().WithMessage("*can only be switched off*");
        await _f.Get<SaveTable>().Execute(seven.Id, new TableRequest("7", seven.AreaId, seven.SortOrder, false, seven.RowVersion), CancellationToken.None);
        _f.Table("7").Active.Should().BeFalse();

        var admin = await _f.Get<GetTablesAdmin>().Execute(CancellationToken.None);
        admin.Tables.Single(t => t.Name == "7").HasHistory.Should().BeTrue();
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case()
    {
        AsAdmin();

        var act = () => _f.Get<SaveTable>().Execute(null, new TableRequest("pool 1", Area("Main"), 99, true), CancellationToken.None);

        await act.Should().ThrowAsync<DomainException>().WithMessage("There is already a table called pool 1.");
    }

    [Fact]
    public async Task Tables_admin_needs_settings_manage()
    {
        // edna (Waiter) is logged in.
        var act = () => _f.Get<GetTablesAdmin>().Execute(CancellationToken.None);

        await act.Should().ThrowAsync<PermissionDeniedException>();
    }

    [Fact]
    public async Task The_demo_seed_has_mau_s_layout_and_open_tickets_including_an_idle_table()
    {
        await using var demo = await TicketsFixture.Create(openDay: false);
        await new Tausi.Infrastructure.Seeding.Seeder(demo.Db, demo.Hasher,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Tausi.Infrastructure.Seeding.Seeder>.Instance).SeedDemoTickets(CancellationToken.None);
        demo.Clock.SetUtcNow(DateTimeOffset.UtcNow);

        var map = await demo.Get<GetTableMap>().Execute(null, CancellationToken.None);

        map.Tables.Should().HaveCount(20);
        map.DayOpen.Should().BeFalse("the demo tickets belong to a demo day that is closed again");
        map.Tables.Single(t => t.Name == "7").Tickets.Should().HaveCount(2);
        map.Tables.Single(t => t.Name == "5").IdleMinutes.Should().BeGreaterThanOrEqualTo(30);
        map.Tables.Where(t => t.Tickets.Count > 0).Should().HaveCountGreaterThan(5);
    }
}
