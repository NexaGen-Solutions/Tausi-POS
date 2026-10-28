using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Tausi.Domain.Service;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(TicketsCollection.Name)]
public class TicketsApiTests(TicketsApp app) : IAsyncLifetime
{
    private static readonly SemaphoreSlim DayGate = new(1, 1);

    public async Task InitializeAsync()
    {
        await DayGate.WaitAsync();
        try
        {
            if (app.Db(db => db.WorkPeriods.Any(w => w.IsOpen))) return;
            var supervisor = await app.PairTerminal();
            await supervisor.LoginWithPin(app.Pins["SAM"]);
            (await supervisor.Http.PostAsync("/api/day/open", null)).EnsureSuccessStatusCode();
        }
        finally
        {
            DayGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Guid Table(string name) => app.Db(db => db.Tables.Single(t => t.Name == name).Id);
    private Guid Item(string name) => app.Db(db => db.MenuItems.Single(i => i.Name == name).Id);

    /// <summary>Beers ask Temperature; the pop-up chooses Cold by default.</summary>
    private Guid[] ColdForBeers(string item) => app.Db(db =>
        db.MenuItems.Where(i => i.Name == item).Select(i => i.Category!.Name).Single() == "Beers"
            ? [db.ModifierOptions.Single(o => o.Name == "Cold").Id]
            : Array.Empty<Guid>());

    private async Task<TerminalClient> Waiter(string name = "edna")
    {
        var terminal = await app.PairTerminal();
        (await terminal.LoginWithPin(app.Pins[name])).EnsureSuccessStatusCode();
        return terminal;
    }

    private async Task<JsonElement> NewTicket(TerminalClient terminal, string? table, string item = "Tusker Lager", int quantity = 1)
    {
        var response = await terminal.Http.PostAsJsonAsync("/api/pos/tickets", new
        {
            tableId = table is null ? (Guid?)null : Table(table),
            lines = new[] { new { menuItemId = Item(item), quantity, optionIds = ColdForBeers(item) } },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    [Fact]
    public async Task Ticket_numbers_never_repeat_under_parallel_first_saves()
    {
        var a = await Waiter("edna");
        var b = await Waiter("Ruth");
        var tables = new[] { "9", "10", "12", "13" };

        var saves = await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            NewTicket(i % 2 == 0 ? a : b, tables[i % tables.Length])));

        saves.Select(s => s.GetProperty("number").GetInt64()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task The_table_map_has_areas_tables_tickets_and_a_version()
    {
        var waiter = await Waiter();
        await NewTicket(waiter, "14", "Guiness", 2);

        var map = await (await waiter.Http.GetAsync("/api/pos/tables")).Json();

        map.GetProperty("version").GetInt64().Should().BeGreaterThan(0);
        map.GetProperty("dayOpen").GetBoolean().Should().BeTrue();
        map.GetProperty("tableIdleMinutes").GetInt32().Should().Be(30);
        map.GetProperty("areas").EnumerateArray().Select(a => a.GetProperty("name").GetString()).Should().Equal("Main", "Pool", "Bar");
        var fourteen = map.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "14");
        var ticket = fourteen.GetProperty("tickets").EnumerateArray().Last();
        ticket.GetProperty("openedBy").GetString().Should().Be("edna");
        ticket.GetProperty("total").GetDecimal().Should().Be(600m);
        ticket.GetProperty("mine").GetBoolean().Should().BeTrue();
        ticket.GetProperty("lock").GetProperty("user").GetString().Should().Be("edna");

        var patch = await (await waiter.Http.GetAsync($"/api/pos/tables?tableIds={Table("14")}")).Json();
        patch.GetProperty("tables").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_second_terminal_gets_a_ticket_read_only_until_the_first_leaves()
    {
        var first = await Waiter("edna");
        var ticket = await NewTicket(first, "15");
        var id = ticket.GetProperty("id").GetGuid();
        var second = await Waiter("Ruth");

        var locked = await (await second.Http.PostAsync($"/api/pos/tickets/{id}/lock", null)).Json();
        locked.GetProperty("editable").GetBoolean().Should().BeFalse();
        locked.GetProperty("message").GetString().Should().Be($"Open on {first.Name} by edna. You can view it but not change it.");

        (await first.Http.DeleteAsync($"/api/pos/tickets/{id}/lock")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await second.Http.PostAsync($"/api/pos/tickets/{id}/lock", null)).Json()).GetProperty("editable").GetBoolean().Should().BeTrue();

        var detail = await (await second.Http.GetAsync($"/api/pos/tickets/{id}")).Json();
        detail.GetProperty("lines").EnumerateArray().Single().GetProperty("addedBy").GetString().Should().Be("edna");
    }

    [Fact]
    public async Task Move_and_merge_work_and_a_merge_needing_approval_works_with_a_supervisor_pin()
    {
        var waiter = await Waiter("CYNTHIA");
        var a = await NewTicket(waiter, "Bar 2", "Tusker Lager", 1);
        var b = await NewTicket(waiter, "16", "Guiness", 1);
        var bId = b.GetProperty("id").GetGuid();

        var moved = await waiter.Http.PostAsJsonAsync($"/api/pos/tickets/{bId}/move", new { toTableId = Table("Bar 2") });
        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        app.Db(db => db.Tickets.Single(k => k.Id == bId).TableId).Should().Be(Table("Bar 2"));

        // Waiters' ticket.merge set to NeedsApproval through the permissions API.
        var admin = await app.AdminClient();
        var staff = await (await admin.GetAsync("/api/admin/staff")).Json();
        var waiterRole = staff.GetProperty("assignableRoles").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Waiter").GetProperty("id").GetGuid();
        await admin.SetPermission(waiterRole, "ticket.merge", "NeedsApproval");
        try
        {
            var merge = new { ticketIds = new[] { bId }, intoTicketId = a.GetProperty("id").GetGuid() };
            var asked = await waiter.Http.PostAsJsonAsync("/api/pos/tickets/merge", merge);
            asked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await asked.Json()).GetProperty("approvalRequired").GetBoolean().Should().BeTrue();

            var approval = await (await waiter.Http.PostAsJsonAsync("/api/auth/approve", new { pin = app.Pins["SAM"], permissionKey = "ticket.merge" })).Json();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/pos/tickets/merge") { Content = JsonContent.Create(merge) };
            request.Headers.Add("X-Approval-Token", approval.GetProperty("approvalToken").GetString());
            var merged = await waiter.Http.SendAsync(request);

            merged.StatusCode.Should().Be(HttpStatusCode.OK, await merged.Content.ReadAsStringAsync());
            (await merged.Json()).GetProperty("total").GetDecimal().Should().Be(550m);
            app.Db(db => db.Tickets.Single(k => k.Id == bId).Status).Should().Be(TicketStatus.Merged);
            app.Db(db => db.AuditEntries.Any(e => e.Action == "ticket.merged" && e.ApprovedByUserId != null)).Should().BeTrue();
        }
        finally
        {
            await admin.SetPermission(waiterRole, "ticket.merge", "Allowed");
        }
    }

    [Fact]
    public async Task Tables_admin_quick_adds_and_refuses_to_switch_off_a_table_in_use()
    {
        var admin = await app.AdminClient();
        var tables = await (await admin.GetAsync("/api/admin/tables")).Json();
        var main = tables.GetProperty("areas").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Main").GetProperty("id").GetGuid();

        var added = await (await admin.PostAsJsonAsync("/api/admin/tables/quick-add", new { from = 15, to = 18, areaId = main })).Json();
        added.GetProperty("added").EnumerateArray().Select(e => e.GetString()).Should().Equal("17", "18");
        added.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()).Should().Equal("15", "16");

        var waiter = await Waiter();
        var ticket = await NewTicket(waiter, "17");
        var seventeen = (await (await admin.GetAsync("/api/admin/tables")).Json()).GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "17");
        var off = await admin.PutAsJsonAsync($"/api/admin/tables/{seventeen.GetProperty("id").GetGuid()}", new
        {
            name = "17", areaId = main, sortOrder = seventeen.GetProperty("sortOrder").GetInt32(), active = false,
            rowVersion = seventeen.GetProperty("rowVersion").GetInt64(),
        });

        off.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await off.Json();
        problem.GetProperty("code").GetString().Should().Be("table_in_use");
        problem.GetProperty("detail").GetString().Should().Contain($"#{ticket.GetProperty("number").GetInt64()}");
    }
}
