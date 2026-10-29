using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Tausi.Application.Security;
using Tausi.Application.Settings;
using Tausi.Application.Tickets;
using Tausi.Domain.Service;
using Tausi.Tests.Support;

namespace Tausi.Tests.Tickets;

public class TableMapTests : IAsyncLifetime
{
    private TicketsFixture _f = null!;
    public async Task InitializeAsync() => _f = await TicketsFixture.Create();
    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<TableMapDto> Map(params Guid[] only) =>
        _f.Get<GetTableMap>().Execute(only.Length == 0 ? null : only, CancellationToken.None);

    [Fact]
    public async Task The_map_lists_mau_s_layout_by_area_then_table_order()
    {
        var map = await Map();

        map.Areas.Select(a => a.Name).Should().Equal("Main", "Pool", "Bar");
        map.Tables.Select(t => t.Name).Should().Equal(
            "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16",
            "Pool 1", "Pool 2", "Bar 1", "Bar 2");
        map.DayOpen.Should().BeTrue();
        map.TableIdleMinutes.Should().Be(30);
        map.ServerTime.Should().Be(_f.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Each_table_shows_its_open_tickets_with_waiter_total_and_whether_they_are_mine()
    {
        var mine = await _f.NewTicket("7", ("Tusker Lager", 2));
        await _f.LoginOn("Ruth", "Pillar 1");
        var ruths = await _f.NewTicket("7", ("Guiness", 1));

        var seven = (await Map()).Tables.Single(t => t.Name == "7");

        seven.Tickets.Select(k => (k.Number, k.OpenedBy, k.Total)).Should().Equal(
            (mine.Number, "edna", 500m), (ruths.Number, "Ruth", 300m));
        seven.Tickets.Select(k => k.Mine).Should().Equal(false, true);
        seven.Tickets[0].Lock!.Terminal.Should().Be("Counter A");
        seven.Tickets[0].Lock!.User.Should().Be("edna");
        (await Map()).Tables.Single(t => t.Name == "4").Tickets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_table_is_mine_when_I_added_a_line_to_someone_elses_ticket()
    {
        var ticket = await _f.NewTicket("7");
        await _f.LoginOn("Ruth", "Pillar 1");
        (await Map()).Tables.Single(t => t.Name == "7").Tickets.Single().Mine.Should().BeFalse();

        _f.Db.OrderLines.Add(new OrderLine
        {
            TicketId = ticket.Id, Name = "Sodas", Quantity = 1, Gross = 100m,
            AddedByUserId = _f.User("Ruth").Id, AddedTerminalId = _f.Session.User!.TerminalId!.Value, AddedAt = _f.Clock.GetUtcNow(),
        });
        await _f.Db.SaveChangesAsync();

        (await Map()).Tables.Single(t => t.Name == "7").Tickets.Single().Mine.Should().BeTrue();
    }

    [Fact]
    public async Task A_table_turns_idle_after_the_setting_without_activity_and_back_after_activity()
    {
        var ticket = await _f.NewTicket("7");
        (await Map()).Tables.Single(t => t.Name == "7").IdleMinutes.Should().BeNull();

        _f.Clock.Advance(TimeSpan.FromMinutes(29));
        (await Map()).Tables.Single(t => t.Name == "7").IdleMinutes.Should().BeNull();
        _f.Clock.Advance(TimeSpan.FromMinutes(13));
        (await Map()).Tables.Single(t => t.Name == "7").IdleMinutes.Should().Be(42);

        // Activity (a move) resets it; the new table is not idle.
        await _f.LoginOn("edna", "Counter A");
        await _f.Get<MoveTicket>().Execute(ticket.Id, new MoveTicketRequest(_f.Table("4").Id), CancellationToken.None);
        (await Map()).Tables.Single(t => t.Name == "4").IdleMinutes.Should().BeNull();
    }

    [Fact]
    public async Task The_idle_limit_comes_from_pos_tableIdleMinutes()
    {
        var settings = await _f.Db.BusinessSettings.SingleAsync();
        settings.OtherJson = """{"pos":{"tableIdleMinutes":1}}""";
        await _f.Db.SaveChangesAsync();
        await _f.NewTicket("7");

        _f.Clock.Advance(TimeSpan.FromSeconds(61));
        var map = await Map();

        map.TableIdleMinutes.Should().Be(1);
        map.Tables.Single(t => t.Name == "7").IdleMinutes.Should().Be(1);
    }

    [Fact]
    public async Task Open_tickets_stay_on_the_map_whatever_day_they_belong_to()
    {
        var old = await _f.NewTicket("7");
        // The day it was opened in ends (in development; cash-up will refuse while tickets are open)
        // and a new day starts.
        await _f.Db.WorkPeriods.ExecuteUpdateAsync(u => u.SetProperty(w => w.IsOpen, false));
        _f.Db.WorkPeriods.Add(new WorkPeriod { OpenedAt = _f.Clock.GetUtcNow(), OpenedByUserId = _f.User("SAM").Id });
        await _f.Db.SaveChangesAsync();

        var seven = (await Map()).Tables.Single(t => t.Name == "7");

        seven.Tickets.Should().ContainSingle().Which.Number.Should().Be(old.Number);
    }

    [Fact]
    public void Idle_minutes_count_from_the_quietest_ticket()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(5);
        var limit = TimeSpan.FromMinutes(30);

        TableIdle.Minutes([now.AddMinutes(-5)], now, limit).Should().BeNull();
        TableIdle.Minutes([now.AddMinutes(-5), now.AddMinutes(-31)], now, limit).Should().Be(31);
        TableIdle.Minutes([now.AddMinutes(-30)], now, limit).Should().Be(30);
    }

    [Fact]
    public async Task Tables_and_tickets_come_from_one_query_however_many_there_are()
    {
        var few = await CountQueries();

        foreach (var table in new[] { "1", "2", "3", "5", "7", "8", "9", "Pool 1", "Bar 1" })
            await _f.NewTicket(table, ("Tusker Lager", 1), ("Sodas 300ml", 2));
        await _f.NewTicket("7", ("Guiness", 1));
        var many = await CountQueries();

        many.Total.Should().Be(few.Total, "the number of queries does not grow with tables or tickets");
        many.Tickets.Should().Be(1, "tables, tickets, lines and locks come from a single query");
    }

    private async Task<(int Total, int Tickets)> CountQueries()
    {
        var counter = new CommandCounter();
        await using var db = _f.NewContext(counter);
        var query = new GetTableMap(db, _f.Session, new TableMapVersion(), new TicketAccess(db, new PermissionService(db, _f.Clock)), _f.Clock);
        await query.Execute(null, CancellationToken.None);
        return (counter.Commands.Count, counter.Commands.Count(c => c.Contains("\"Tickets\"")));
    }

    [Fact]
    public async Task Patching_fetches_only_the_asked_tables_with_the_current_version()
    {
        var versions = _f.Get<TableMapVersion>();
        await _f.NewTicket("7");
        var current = versions.Current;

        var patch = await Map(_f.Table("7").Id);

        patch.Tables.Should().ContainSingle().Which.Name.Should().Be("7");
        patch.Version.Should().Be(current);
    }

    [Fact]
    public async Task Inactive_tables_and_areas_are_left_off_the_map()
    {
        await _f.Db.Tables.Where(t => t.Name == "16").ExecuteUpdateAsync(u => u.SetProperty(t => t.Active, false));
        await _f.Db.Areas.Where(a => a.Name == "Bar").ExecuteUpdateAsync(u => u.SetProperty(a => a.Active, false));

        var map = await Map();

        map.Tables.Select(t => t.Name).Should().NotContain(["16", "Bar 1", "Bar 2"]);
        map.Areas.Select(a => a.Name).Should().Equal("Main", "Pool");
    }

    [Fact]
    public async Task Merged_and_settled_tickets_are_not_on_the_map_and_takeaway_lists_its_own()
    {
        var settled = await _f.NewTicket("7");
        await _f.Db.Tickets.Where(k => k.Id == settled.Id).ExecuteUpdateAsync(u => u.SetProperty(k => k.Status, TicketStatus.Settled));
        var takeaway = await _f.Get<StartTicket>().Execute(
            new StartTicketRequest(null, "Otieno", [new NewLineRequest(_f.Item("Pineapple Punch"), 1)]), CancellationToken.None);

        (await Map()).Tables.SelectMany(t => t.Tickets).Should().BeEmpty();
        var list = await _f.Get<GetTakeawayTickets>().Execute(CancellationToken.None);
        list.Should().ContainSingle().Which.Should().Match<TicketSummaryDto>(k =>
            k.Number == takeaway.Number && k.CustomerName == "Otieno" && k.Total == 270m && k.OpenedBy == "edna");
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
