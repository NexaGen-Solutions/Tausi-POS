using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>ticket.others over the API, with the default grid: waiters work on their own tickets only.</summary>
[Collection(OwnTicketsCollection.Name)]
public class TicketOwnershipApiTests(OwnTicketsApp app) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        var supervisor = await app.PairTerminal();
        await supervisor.LoginWithPin(app.Pins["SAM"]);
        if (!app.Db(db => db.WorkPeriods.Any(w => w.IsOpen)))
            (await supervisor.Http.PostAsync("/api/day/open", null)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Guid Table(string name) => app.Db(db => db.Tables.Single(t => t.Name == name).Id);
    private Guid Item(string name) => app.Db(db => db.MenuItems.Single(i => i.Name == name).Id);

    private async Task<TerminalClient> Waiter(string name)
    {
        var terminal = await app.PairTerminal();
        (await terminal.LoginWithPin(app.Pins[name])).EnsureSuccessStatusCode();
        return terminal;
    }

    private Task<HttpResponseMessage> Start(TerminalClient terminal, string table) =>
        terminal.Http.PostAsJsonAsync("/api/pos/tickets", new
        {
            tableId = Table(table),
            lines = new[] { new { menuItemId = Item("Sodas 300ml"), quantity = 1, optionIds = Array.Empty<Guid>() } },
        });

    [Fact]
    public async Task A_waiter_views_another_waiters_ticket_but_can_not_change_it_or_start_one_on_that_table()
    {
        var edna = await Waiter("edna");
        var started = await Start(edna, "12");
        started.StatusCode.Should().Be(HttpStatusCode.OK, await started.Content.ReadAsStringAsync());
        var id = (await started.Json()).GetProperty("id").GetGuid();
        (await edna.Http.DeleteAsync($"/api/pos/tickets/{id}/lock")).EnsureSuccessStatusCode();

        var ruth = await Waiter("Ruth");
        (await ruth.Http.GetAsync($"/api/pos/tickets/{id}")).StatusCode.Should().Be(HttpStatusCode.OK, "she may look");
        var state = await (await ruth.Http.PostAsync($"/api/pos/tickets/{id}/lock", null)).Json();
        state.GetProperty("editable").GetBoolean().Should().BeFalse();
        var add = await ruth.Http.PostAsJsonAsync($"/api/pos/tickets/{id}/lines",
            new { menuItemId = Item("Sodas 300ml"), quantity = 1, optionIds = Array.Empty<Guid>() });
        add.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await add.Json()).GetProperty("code").GetString().Should().Be("ticket_not_yours");
        var second = await Start(ruth, "12");
        second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await second.Json()).GetProperty("code").GetString().Should().Be("table_taken");

        var admin = await app.AdminClient();
        await admin.SetSetting("pos.showOthersTickets", false);
        try
        {
            (await ruth.Http.GetAsync($"/api/pos/tickets/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var map = await (await ruth.Http.GetAsync("/api/pos/tables")).Json();
            var tile = map.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "12");
            var hidden = tile.GetProperty("tickets")[0];
            hidden.GetProperty("private").GetBoolean().Should().BeTrue();
            hidden.GetProperty("openedBy").GetString().Should().BeEmpty();
            hidden.GetProperty("total").GetDecimal().Should().Be(0);

            var sam = await Waiter("SAM");
            (await (await sam.Http.PostAsync($"/api/pos/tickets/{id}/lock", null)).Json()).GetProperty("editable").GetBoolean()
                .Should().BeTrue("supervisors work on any ticket");
        }
        finally
        {
            await admin.SetSetting("pos.showOthersTickets", true);
        }
    }
}
