using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Domain.Ledger;
using Tausi.Domain.Service;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>Settling over HTTP on PostgreSQL: payments, complete, receipts and the ledger.</summary>
[Collection(TicketsCollection.Name)]
public class SettleApiTests(TicketsApp app) : IAsyncLifetime
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

    private async Task<TerminalClient> On(string name)
    {
        var terminal = await app.PairTerminal();
        var printer = app.Db(db => db.Printers.Single(p => p.Name == "Ticket Printer 1").Id);
        app.Db(db => db.Terminals.Where(t => t.Id == terminal.TerminalId)
            .ExecuteUpdate(u => u.SetProperty(t => t.ReceiptPrinterId, printer).SetProperty(t => t.DispensePrinterId, printer)));
        (await terminal.LoginWithPin(app.Pins[name])).EnsureSuccessStatusCode();
        return terminal;
    }

    /// <summary>
    /// A cashier on a new terminal with a shift open on it. Tests share the database, so a shift
    /// this cashier left open on another test's terminal is closed first.
    /// </summary>
    private async Task<TerminalClient> Cashier(string name = "CAROL")
    {
        var userId = app.Db(db => db.Users.Single(u => u.Name == name).Id);
        app.Db(db => db.CashierShifts.Where(s => s.CashierUserId == userId && s.Status == Domain.Cash.ShiftStatus.Open)
            .ExecuteUpdate(u => u.SetProperty(s => s.Status, Domain.Cash.ShiftStatus.Closed)));
        var terminal = await On(name);
        (await terminal.Http.PostAsJsonAsync("/api/pos/shifts", new { @float = new { total = 0 } })).EnsureSuccessStatusCode();
        return terminal;
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    private static HttpRequestMessage Post(string url, string key, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    /// <summary>edna orders punch on a takeaway ticket, prints, and leaves it; the cashier opens it.</summary>
    private async Task<Guid> Printed(TerminalClient waiter, TerminalClient cashier, int punches = 1, string? table = null)
    {
        var ticket = await Ok(await waiter.Http.PostAsJsonAsync("/api/pos/tickets", new
        {
            tableId = table is null ? (Guid?)null : Table(table),
            customerName = table is null ? "Otieno" : null,
            lines = new[] { new { menuItemId = Item("Pineapple Punch"), quantity = punches } },
        }));
        var id = ticket.GetProperty("id").GetGuid();
        await Ok(await waiter.Http.PostAsync($"/api/pos/tickets/{id}/print", null));
        (await waiter.Http.DeleteAsync($"/api/pos/tickets/{id}/lock")).EnsureSuccessStatusCode();
        (await Ok(await cashier.Http.PostAsync($"/api/pos/tickets/{id}/lock", null))).GetProperty("editable").GetBoolean().Should().BeTrue();
        return id;
    }

    [Fact]
    public async Task M_pesa_and_cash_settle_once_with_a_receipt_a_free_table_and_a_balanced_entry()
    {
        var edna = await On("edna");
        var carol = await Cashier();
        var id = await Printed(edna, carol, punches: 5, table: "Bar 1"); // 1,350

        var mpesa = await Ok(await carol.Http.PostAsJsonAsync($"/api/pos/tickets/{id}/payments",
            new { type = "Mpesa", amount = 1000, mpesaCode = "sjk3int001" }));
        mpesa.GetProperty("remaining").GetDecimal().Should().Be(350m);
        var map = await Ok(await carol.Http.GetAsync($"/api/pos/tables?tableIds={Table("Bar 1")}"));
        map.GetProperty("tables")[0].GetProperty("tickets")[0].GetProperty("left").GetDecimal().Should().Be(350m);
        var cash = await Ok(await carol.Http.PostAsJsonAsync($"/api/pos/tickets/{id}/payments", new { type = "Cash", tendered = 500 }));
        cash.GetProperty("payments")[1].GetProperty("change").GetDecimal().Should().Be(150m);

        var key = Guid.NewGuid().ToString();
        var settled = await Ok(await carol.Http.SendAsync(Post($"/api/pos/tickets/{id}/settle", key)));
        var again = await carol.Http.SendAsync(Post($"/api/pos/tickets/{id}/settle", key));
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        again.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
        (await again.Json()).GetProperty("receiptNumber").GetInt64().Should().Be(settled.GetProperty("receiptNumber").GetInt64());
        settled.GetProperty("change").GetDecimal().Should().Be(150m);

        app.Db(db => db.JournalEntries.Count(e => e.SourceId == id.ToString())).Should().Be(1);
        var lines = app.Db(db => db.JournalEntries.Where(e => e.SourceId == id.ToString()).SelectMany(e => e.Lines).Include(l => l.Account).ToList());
        lines.Sum(l => l.Debit).Should().Be(lines.Sum(l => l.Credit)).And.Be(1350m);
        lines.Where(l => l.Account!.Code == LedgerAccountCodes.Cash).Sum(l => l.Debit).Should().Be(350m);
        app.Db(db => db.Tickets.Single(k => k.Id == id).Status).Should().Be(TicketStatus.Settled);

        var list = await Ok(await carol.Http.GetAsync("/api/pos/tickets/settled"));
        list.EnumerateArray().Should().Contain(s => s.GetProperty("id").GetGuid() == id);
        (await carol.Http.PostAsync($"/api/pos/tickets/{id}/receipt/reprint", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        app.Db(db => db.PrintJobs.Count(j => j.TicketId == id && j.Kind == Domain.Printing.PrintJobKind.Receipt)).Should().Be(2);

        // The code can't be used again, on any ticket.
        var other = await Printed(edna, carol);
        var reused = await carol.Http.PostAsJsonAsync($"/api/pos/tickets/{other}/payments", new { type = "Mpesa", amount = 100, mpesaCode = "SJK3INT001" });
        reused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await reused.Json()).GetProperty("detail").GetString().Should().StartWith("Code SJK3INT001 was already used on #");
    }

    [Fact]
    public async Task A_waiter_cant_settle()
    {
        var edna = await On("edna");
        var ticket = await Ok(await edna.Http.PostAsJsonAsync("/api/pos/tickets", new
        {
            customerName = "Waiter test", lines = new[] { new { menuItemId = Item("Pineapple Punch"), quantity = 1 } },
        }));
        var response = await edna.Http.PostAsJsonAsync($"/api/pos/tickets/{ticket.GetProperty("id").GetGuid()}/payments", new { type = "Card", amount = 270 });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Json()).GetProperty("approvalRequired").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Two_settles_of_one_ticket_at_once_settle_it_once()
    {
        var edna = await On("edna");
        var carol = await Cashier();
        var id = await Printed(edna, carol);
        await Ok(await carol.Http.PostAsJsonAsync($"/api/pos/tickets/{id}/payments", new { type = "Card", amount = 270 }));

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            carol.Http.SendAsync(Post($"/api/pos/tickets/{id}/settle", Guid.NewGuid().ToString()))));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        app.Db(db => db.JournalEntries.Count(e => e.SourceId == id.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task Receipt_numbers_never_repeat_when_settled_in_parallel()
    {
        var edna = await On("edna");
        var carol = await Cashier();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = await Printed(edna, carol);
            await Ok(await carol.Http.PostAsJsonAsync($"/api/pos/tickets/{id}/payments", new { type = "Card", amount = 270 }));
            ids.Add(id);
        }

        var settled = await Task.WhenAll(ids.Select(async id => await Ok(await carol.Http.PostAsync($"/api/pos/tickets/{id}/settle", null))));

        settled.Select(s => s.GetProperty("receiptNumber").GetInt64()).Should().OnlyHaveUniqueItems().And.HaveCount(5);
    }

    [Fact]
    public async Task Close_empty_needs_day_open_close_and_a_ticket_with_nothing_on_it()
    {
        var edna = await On("edna");
        var ticket = await Ok(await edna.Http.PostAsJsonAsync("/api/pos/tickets", new
        {
            customerName = "Empty", lines = new[] { new { menuItemId = Item("Pineapple Punch"), quantity = 1 } },
        }));
        var id = ticket.GetProperty("id").GetGuid();
        var line = ticket.GetProperty("lines")[0].GetProperty("id").GetGuid();

        (await edna.Http.PostAsync($"/api/pos/tickets/{id}/close-empty", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var mau = await On("MAU");
        (await mau.Http.PostAsync($"/api/pos/tickets/{id}/close-empty", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Ok(await edna.Http.DeleteAsync($"/api/pos/tickets/{id}/lines/{line}"));
        (await mau.Http.PostAsync($"/api/pos/tickets/{id}/close-empty", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        app.Db(db => db.Tickets.Single(k => k.Id == id).Status).Should().Be(TicketStatus.Cancelled);
    }
}
