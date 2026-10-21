using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Domain.Printing;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(TicketsCollection.Name)]
public class OrderScreenApiTests(TicketsApp app) : IAsyncLifetime
{
    private static readonly SemaphoreSlim DayGate = new(1, 1);

    public async Task InitializeAsync()
    {
        await DayGate.WaitAsync();
        try
        {
            if (app.Db(db => db.WorkPeriods.Any(w => w.IsOpen))) return;
            var supervisor = await app.PairTerminal();
            await supervisor.LoginWithPin(app.Pins["MAU"]);
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
    private Guid Option(string name) => app.Db(db => db.ModifierOptions.Single(o => o.Name == name).Id);

    /// <summary>A waiter on a terminal whose receipt and dispense printer is Ticket Printer 1 (a File printer).</summary>
    private async Task<TerminalClient> Waiter(string name = "edna")
    {
        var terminal = await app.PairTerminal();
        var printer = app.Db(db => db.Printers.Single(p => p.Name == "Ticket Printer 1").Id);
        app.Db(db => db.Terminals.Where(t => t.Id == terminal.TerminalId)
            .ExecuteUpdate(u => u.SetProperty(t => t.ReceiptPrinterId, printer).SetProperty(t => t.DispensePrinterId, printer)));
        (await terminal.LoginWithPin(app.Pins[name])).EnsureSuccessStatusCode();
        return terminal;
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    [Fact]
    public async Task Lines_are_saved_as_they_happen_with_the_server_price()
    {
        var waiter = await Waiter();
        // A price in the request is ignored: there is no such field, and the menu decides.
        var ticket = await Ok(await waiter.Http.PostAsJsonAsync("/api/pos/tickets", new
        {
            tableId = Table("10"),
            lines = new[] { new { menuItemId = Item("Tusker Lager"), quantity = 2, optionIds = new[] { Option("Cold") }, unitPrice = 1 } },
        }));
        var id = ticket.GetProperty("id").GetGuid();
        ticket.GetProperty("total").GetDecimal().Should().Be(500m);

        var added = await Ok(await waiter.Http.PostAsJsonAsync($"/api/pos/tickets/{id}/lines",
            new { menuItemId = Item("Pineapple Punch"), optionIds = Array.Empty<Guid>(), quantity = 1 }));
        added.GetProperty("total").GetDecimal().Should().Be(770m);
        added.GetProperty("newLines").GetInt32().Should().Be(2);

        var punch = added.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("name").GetString() == "Pineapple Punch").GetProperty("id").GetGuid();
        var patched = await Ok(await waiter.Http.PatchAsJsonAsync($"/api/pos/tickets/{id}/lines/{punch}", new { quantity = 3 }));
        patched.GetProperty("total").GetDecimal().Should().Be(1310m);
        var removed = await Ok(await waiter.Http.DeleteAsync($"/api/pos/tickets/{id}/lines/{punch}"));
        removed.GetProperty("total").GetDecimal().Should().Be(500m);

        var missingChoice = await waiter.Http.PostAsJsonAsync($"/api/pos/tickets/{id}/lines", new { menuItemId = Item("Guiness"), quantity = 1 });
        missingChoice.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Two_presses_at_once_print_one_round_and_another_terminal_can_not_print()
    {
        var waiter = await Waiter();
        var ticket = await Ok(await waiter.Http.PostAsJsonAsync("/api/pos/tickets", new
        {
            tableId = Table("12"),
            lines = new[] { new { menuItemId = Item("Pineapple Punch"), quantity = 1 } },
        }));
        var id = ticket.GetProperty("id").GetGuid();

        var presses = await Task.WhenAll(waiter.Http.PostAsync($"/api/pos/tickets/{id}/print", null), waiter.Http.PostAsync($"/api/pos/tickets/{id}/print", null));

        presses.Select(p => p.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var refused = await presses.Single(p => p.StatusCode == HttpStatusCode.Conflict).Json();
        refused.GetProperty("code").GetString().Should().Be("nothing_new");
        app.Db(db => db.PrintJobs.Count(j => j.TicketId == id)).Should().Be(2);

        var other = await Waiter("Ruth");
        await other.Http.PostAsync($"/api/pos/tickets/{id}/lock", null);
        var elsewhere = await other.Http.PostAsync($"/api/pos/tickets/{id}/reprint-bill", null);
        elsewhere.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await elsewhere.Json()).GetProperty("code").GetString().Should().Be("ticket_locked");
    }

    [Fact]
    public async Task A_waiters_reprint_goes_through_a_supervisor_and_a_failed_job_can_be_retried()
    {
        var waiter = await Waiter("CYNTHIA");
        var ticket = await Ok(await waiter.Http.PostAsJsonAsync("/api/pos/tickets", new
        {
            tableId = Table("13"),
            lines = new[] { new { menuItemId = Item("Sodas 300ml"), quantity = 2 } },
        }));
        var id = ticket.GetProperty("id").GetGuid();
        await Ok(await waiter.Http.PostAsync($"/api/pos/tickets/{id}/print", null));

        var asked = await waiter.Http.PostAsync($"/api/pos/tickets/{id}/reprint-bill", null);
        asked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await asked.Json()).GetProperty("approvalRequired").GetBoolean().Should().BeTrue();

        var approval = await Ok(await waiter.Http.PostAsJsonAsync("/api/auth/approve", new { pin = app.Pins["MAU"], permissionKey = "ticket.reprint" }));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/pos/tickets/{id}/reprint-bill");
        request.Headers.Add("X-Approval-Token", approval.GetProperty("approvalToken").GetString());
        (await Ok(await waiter.Http.SendAsync(request))).GetProperty("billNumber").GetInt32().Should().Be(2);

        var bill = app.Db(db => db.PrintJobs.Where(j => j.TicketId == id && j.Kind == PrintJobKind.Bill).OrderBy(j => j.CreatedAt).First().Id);
        app.Db(db => db.PrintJobs.Where(j => j.Id == bill).ExecuteUpdate(u => u.SetProperty(j => j.Status, PrintJobStatus.Failed)));
        (await Ok(await waiter.Http.GetAsync($"/api/pos/tickets/{id}"))).GetProperty("failedJobs").GetArrayLength().Should().Be(1);
        (await waiter.Http.PostAsync($"/api/pos/tickets/{id}/print-jobs/{bill}/retry", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        app.Db(db => db.PrintJobs.Single(j => j.Id == bill).Status).Should().NotBe(PrintJobStatus.Failed);
    }
}
