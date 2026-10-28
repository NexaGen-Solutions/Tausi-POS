using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Domain.Cash;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>Cashier shifts, sign-off and My sales over HTTP (PostgreSQL).</summary>
[Collection(TicketsCollection.Name)]
public class CashupApiTests(TicketsApp app) : IAsyncLifetime
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

    private async Task<TerminalClient> On(string name)
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

    /// <summary>CAROL with a fresh shift on a new terminal (any shift she left open elsewhere is closed first).</summary>
    private async Task<(TerminalClient Carol, Guid ShiftId)> CarolWithShift(decimal openingFloat)
    {
        var userId = app.Db(db => db.Users.Single(u => u.Name == "CAROL").Id);
        app.Db(db => db.CashierShifts.Where(s => s.CashierUserId == userId && s.Status == ShiftStatus.Open)
            .ExecuteUpdate(u => u.SetProperty(s => s.Status, ShiftStatus.Closed)));
        var carol = await On("CAROL");
        var shift = await Ok(await carol.Http.PostAsJsonAsync("/api/pos/shifts", new { @float = new { total = openingFloat } }));
        return (carol, shift.GetProperty("id").GetGuid());
    }

    private async Task<string> Token(TerminalClient terminal, string pinOf) =>
        (await Ok(await terminal.Http.PostAsJsonAsync("/api/auth/approve", new { pin = app.Pins[pinOf], permissionKey = "day.open_close" })))
            .GetProperty("approvalToken").GetString()!;

    private static HttpRequestMessage Close(Guid shiftId, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/pos/shifts/{shiftId}/close");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (token is not null) request.Headers.Add("X-Approval-Token", token);
        return request;
    }

    [Fact]
    public async Task A_shift_is_counted_blind_and_closed_with_a_supervisors_sign_off()
    {
        var (carol, shiftId) = await CarolWithShift(2000m);
        var current = await Ok(await carol.Http.GetAsync("/api/pos/shifts/current"));
        current.GetProperty("shift").GetProperty("expected").ValueKind.Should().Be(JsonValueKind.Null, "the count is blind");

        var counted = await Ok(await carol.Http.PostAsJsonAsync($"/api/pos/shifts/{shiftId}/count",
            new { count = new { notes = new Dictionary<string, int> { ["1000"] = 2 }, coins = 0 } }));
        counted.GetProperty("expected").GetDecimal().Should().Be(2000m);
        counted.GetProperty("difference").GetDecimal().Should().Be(0m);

        var noToken = await carol.Http.SendAsync(Close(shiftId, null));
        noToken.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noToken.Json()).GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
        (await noToken.Json()).GetProperty("permission").GetString().Should().Be("day.open_close");
        var own = await carol.Http.PostAsJsonAsync("/api/auth/approve", new { pin = app.Pins["CAROL"], permissionKey = "day.open_close" });
        own.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var closed = await Ok(await carol.Http.SendAsync(Close(shiftId, await Token(carol, "SAM"))));
        closed.GetProperty("status").GetString().Should().Be("Closed");
        closed.GetProperty("signedOffBy").GetString().Should().Be("SAM");
        app.Db(db => db.JournalEntries.Where(e => e.ShiftId == shiftId).SelectMany(e => e.Lines).ToList())
            .Should().Match<List<Domain.Ledger.JournalLine>>(l => l.Sum(x => x.Debit) == l.Sum(x => x.Credit));
    }

    [Fact]
    public async Task Two_sign_offs_of_one_shift_at_once_close_it_once()
    {
        var (carol, shiftId) = await CarolWithShift(500m);
        await Ok(await carol.Http.PostAsJsonAsync($"/api/pos/shifts/{shiftId}/count", new { count = new { total = 500 } }));
        var first = await Token(carol, "SAM");
        var second = await Token(carol, "Administrator");

        var results = await Task.WhenAll(carol.Http.SendAsync(Close(shiftId, first)), carol.Http.SendAsync(Close(shiftId, second)));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        app.Db(db => db.JournalEntries.Count(e => e.ShiftId == shiftId && e.Memo == "Cash-up")).Should().Be(1);
    }

    [Fact]
    public async Task My_sales_never_returns_another_users_figures()
    {
        var edna = await On("edna");
        var cynthia = await On("CYNTHIA");
        var item = app.Db(db => db.MenuItems.Single(i => i.Name == "Pineapple Punch").Id);
        await Ok(await edna.Http.PostAsJsonAsync("/api/pos/tickets", new { customerName = "Edna's", lines = new[] { new { menuItemId = item, quantity = 1 } } }));
        await Ok(await cynthia.Http.PostAsJsonAsync("/api/pos/tickets", new { customerName = "Cynthia's", lines = new[] { new { menuItemId = item, quantity = 2 } } }));

        var mine = await Ok(await edna.Http.GetAsync("/api/pos/my-sales"));
        mine.GetProperty("name").GetString().Should().Be("edna");
        var ednaId = app.Db(db => db.Users.Single(u => u.Name == "edna").Id);
        var ednaLines = app.Db(db => db.OrderLines.Count(l => l.AddedByUserId == ednaId && l.Status != Domain.Service.OrderLineStatus.Cancelled));
        mine.GetProperty("lines").GetArrayLength().Should().BeLessThanOrEqualTo(ednaLines);
        var ednaTicketIds = app.Db(db => db.OrderLines.Where(l => l.AddedByUserId == ednaId).Select(l => l.TicketId).Distinct().ToList());
        mine.GetProperty("lines").EnumerateArray().Should().OnlyContain(l => ednaTicketIds.Contains(l.GetProperty("ticketId").GetGuid()));
        mine.GetProperty("openBills").EnumerateArray().Should().NotContain(b => b.GetProperty("where").GetString() == "Takeaway · Cynthia's");

        (await edna.Http.GetAsync("/api/pos/day")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "only day.open_close sees the day");
        (await edna.Http.PostAsync("/api/pos/my-sales/print", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
