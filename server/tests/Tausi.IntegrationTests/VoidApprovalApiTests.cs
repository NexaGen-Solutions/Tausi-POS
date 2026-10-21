using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.Domain.Service;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>Voids, gifts, discounts, price changes and approval requests over HTTP (PostgreSQL).</summary>
[Collection(TicketsCollection.Name)]
public class VoidApprovalApiTests(TicketsApp app) : IAsyncLifetime
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

    /// <summary>Someone on a new terminal printing to Ticket Printer 1 (a File printer).</summary>
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

    /// <summary>A ticket on a free table with 2 Tusker (Cold), printed. Returns the ticket id and the line id.</summary>
    private async Task<(Guid Ticket, Guid Line)> PrintedTusker(TerminalClient waiter, string table)
    {
        var ticket = await Ok(await waiter.Http.PostAsJsonAsync("/api/pos/tickets", new
        {
            tableId = Table(table),
            lines = new[] { new { menuItemId = Item("Tusker Lager"), quantity = 2, optionIds = new[] { Option("Cold") } } },
        }));
        var id = ticket.GetProperty("id").GetGuid();
        var printed = await Ok(await waiter.Http.PostAsync($"/api/pos/tickets/{id}/print", null));
        return (id, printed.GetProperty("lines")[0].GetProperty("id").GetGuid());
    }

    private static HttpRequestMessage VoidRequest(Guid ticket, Guid line, string key, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/pos/tickets/{ticket}/lines/{line}/void")
        {
            Content = JsonContent.Create(new { quantity = 1, reason = "Wrong item" }),
        };
        request.Headers.Add("Idempotency-Key", key);
        if (token is not null) request.Headers.Add("X-Approval-Token", token);
        return request;
    }

    [Fact]
    public async Task A_waiters_void_needs_a_supervisor_pin_that_works_once_and_never_their_own()
    {
        var edna = await On("edna");
        var (ticket, line) = await PrintedTusker(edna, "2");
        var key = Guid.NewGuid().ToString();

        var first = await edna.Http.SendAsync(VoidRequest(ticket, line, key));
        first.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await first.Json()).GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
        (await first.Json()).GetProperty("permission").GetString().Should().Be("order.void");

        var own = await edna.Http.PostAsJsonAsync("/api/auth/approve", new { pin = app.Pins["edna"], permissionKey = "order.void" });
        own.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await own.Json()).GetProperty("code").GetString().Should().Be("own_request");

        var token = (await Ok(await edna.Http.PostAsJsonAsync("/api/auth/approve",
            new { pin = app.Pins["MAU"], permissionKey = "order.void" }))).GetProperty("approvalToken").GetString();
        // The failed try wasn't stored, so the same key runs again with the token.
        var voided = await Ok(await edna.Http.SendAsync(VoidRequest(ticket, line, key, token)));
        voided.GetProperty("total").GetDecimal().Should().Be(250m);
        voided.GetProperty("lines").EnumerateArray().Should().Contain(l => l.GetProperty("status").GetString() == "Voided");

        // A repeat gets the stored result; a new key with the used token is refused.
        var repeat = await edna.Http.SendAsync(VoidRequest(ticket, line, key));
        repeat.StatusCode.Should().Be(HttpStatusCode.OK);
        repeat.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
        (await edna.Http.SendAsync(VoidRequest(ticket, line, Guid.NewGuid().ToString(), token))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var log = app.Db(db => db.VoidLogs.Single(v => v.TicketId == ticket));
        log.RequestedByUserId.Should().Be(app.Db(db => db.Users.Single(u => u.Name == "edna").Id));
        log.ApprovedByUserId.Should().Be(app.Db(db => db.Users.Single(u => u.Name == "MAU").Id));
        app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.LineVoided && a.EntityId == ticket.ToString())).Should().Be(1);
        app.Db(db => db.PrintJobs.Count(j => j.TicketId == ticket && j.Kind == Domain.Printing.PrintJobKind.Void)).Should().Be(1);
    }

    [Fact]
    public async Task A_request_is_listed_for_approvers_and_two_supervisors_approving_at_once_make_one_void()
    {
        var mau = await On("MAU");
        var admin = await On("Administrator");
        var edna = await On("edna");
        var (ticket, line) = await PrintedTusker(edna, "4");

        var approvers = await Ok(await edna.Http.GetAsync("/api/pos/approvals/approvers?kind=Void"));
        approvers.EnumerateArray().Select(a => a.GetProperty("name").GetString()).Should().Contain(["MAU", "Administrator"]).And.NotContain("edna");

        var request = await Ok(await edna.Http.PostAsJsonAsync("/api/pos/approvals", new
        {
            kind = "Void", ticketId = ticket, lineId = line,
            payload = new { quantity = 1, reason = "Wrong item" },
            preferredApproverId = app.Db(db => db.Users.Single(u => u.Name == "MAU").Id),
        }));
        var id = request.GetProperty("id").GetGuid();
        request.GetProperty("status").GetString().Should().Be("Pending");

        var forMau = await Ok(await mau.Http.GetAsync("/api/pos/approvals?status=pending"));
        forMau.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id).GetProperty("sentToMe").GetBoolean().Should().BeTrue();
        var detail = await Ok(await edna.Http.GetAsync($"/api/pos/tickets/{ticket}"));
        detail.GetProperty("lines")[0].GetProperty("pending").GetProperty("kind").GetString().Should().Be("Void");

        var results = await Task.WhenAll(
            mau.Http.PostAsync($"/api/pos/approvals/{id}/approve", null),
            admin.Http.PostAsync($"/api/pos/approvals/{id}/approve", null));

        results.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        app.Db(db => db.VoidLogs.Count(v => v.TicketId == ticket)).Should().Be(1);
        app.Db(db => db.OrderLines.Count(l => l.TicketId == ticket && l.Status == OrderLineStatus.Voided)).Should().Be(1);
        var decided = await Ok(await mau.Http.GetAsync("/api/pos/approvals?status=decided"));
        decided.EnumerateArray().Should().Contain(r => r.GetProperty("id").GetGuid() == id && r.GetProperty("status").GetString() == "Approved");
    }

    [Fact]
    public async Task Reject_and_cancel_over_http()
    {
        var mau = await On("MAU");
        var edna = await On("edna");
        var (ticket, line) = await PrintedTusker(edna, "6");
        async Task<Guid> Ask() => (await Ok(await edna.Http.PostAsJsonAsync("/api/pos/approvals", new
        {
            kind = "Void", ticketId = ticket, lineId = line, payload = new { quantity = 2, reason = "Customer changed mind" },
        }))).GetProperty("id").GetGuid();

        var first = await Ask();
        var rejected = await Ok(await mau.Http.PostAsJsonAsync($"/api/pos/approvals/{first}/reject", new { note = "Already poured" }));
        rejected.GetProperty("status").GetString().Should().Be("Rejected");
        rejected.GetProperty("decisionNote").GetString().Should().Be("Already poured");

        var second = await Ask();
        (await mau.Http.PostAsync($"/api/pos/approvals/{second}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Ok(await edna.Http.PostAsync($"/api/pos/approvals/{second}/cancel", null))).GetProperty("status").GetString().Should().Be("Cancelled");

        (await Ok(await edna.Http.GetAsync($"/api/pos/tickets/{ticket}"))).GetProperty("total").GetDecimal().Should().Be(500m);
    }

    [Fact]
    public async Task Discount_gift_and_price_endpoints()
    {
        var admin = await On("Administrator");
        var (ticket, line) = await PrintedTusker(admin, "Pool 2");

        var gifted = await Ok(await admin.Http.PostAsJsonAsync($"/api/pos/tickets/{ticket}/lines/{line}/gift", new { quantity = 1, reason = "House" }));
        gifted.GetProperty("total").GetDecimal().Should().Be(250m);
        var paid = gifted.GetProperty("lines").EnumerateArray().Single(l => !l.GetProperty("isGift").GetBoolean()).GetProperty("id").GetGuid();
        var priced = await Ok(await admin.Http.PostAsJsonAsync($"/api/pos/tickets/{ticket}/lines/{paid}/price",
            new { quantity = 1, newUnitPrice = 200, reason = "Happy hour" }));
        priced.GetProperty("total").GetDecimal().Should().Be(200m);
        var discounted = await Ok(await admin.Http.PostAsJsonAsync($"/api/pos/tickets/{ticket}/discount",
            new { kind = "Percent", value = 10, reason = "Regular customer" }));
        discounted.GetProperty("total").GetDecimal().Should().Be(180m);
        discounted.GetProperty("discountLabel").GetString().Should().Be("Discount 10% (Regular customer)");
        var removed = await Ok(await admin.Http.PostAsJsonAsync($"/api/pos/tickets/{ticket}/discount", new { kind = (string?)null, value = 0 }));
        removed.GetProperty("total").GetDecimal().Should().Be(200m);
    }
}
