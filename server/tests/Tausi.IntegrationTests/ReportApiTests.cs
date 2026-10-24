using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Reports;
using Tausi.Domain.Service;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>Reports over HTTP (PostgreSQL, the demo data): the list, running, Excel, CSV, PDF, the receipt printer and the ticket detail.</summary>
[Collection(AppCollection.Name)]
public class ReportApiTests(TausiApp app) : IAsyncLifetime
{
    /// <summary>The demo day open, so there are work periods and tickets to report on.</summary>
    public async Task InitializeAsync()
    {
        if (app.Db(db => db.WorkPeriods.Any(w => w.IsOpen))) return;
        var supervisor = await app.PairTerminal();
        await supervisor.LoginWithPin(app.Pins["MAU"]);
        (await supervisor.Http.PostAsync("/api/day/open", null)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>edna opens a ticket with 2 Pineapple Punch (an open ticket counts in the open day's sales).</summary>
    private async Task<Guid> Ticket()
    {
        var edna = await app.PairTerminal();
        (await edna.LoginWithPin(app.Pins["edna"])).EnsureSuccessStatusCode();
        var punch = app.Db(db => db.MenuItems.Single(i => i.Name == "Pineapple Punch").Id);
        return (await Ok(await edna.Http.PostAsJsonAsync("/api/pos/tickets",
            new { customerName = "Reports", lines = new[] { new { menuItemId = punch, quantity = 2 } } }))).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    [Fact]
    public async Task The_administrator_runs_exports_and_prints_sales_by_user()
    {
        await Ticket();
        var admin = await app.AdminClient();
        var list = await Ok(await admin.GetAsync("/api/admin/reports"));
        list.EnumerateArray().Select(r => r.GetProperty("key").GetString()).Should().Equal(ReportService.Order);
        var options = await Ok(await admin.GetAsync("/api/admin/reports/options"));
        options.GetProperty("periods").GetArrayLength().Should().BeGreaterThan(0);

        var result = await Ok(await admin.PostAsJsonAsync("/api/admin/reports/sales-by-user/run", new { creditTo = "AddedBy" }));
        result.GetProperty("title").GetString().Should().Be("Sales by user");
        var table = result.GetProperty("tables")[0];
        table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("type").GetString()).Should()
            .Equal("Ticket", "Time", "Text", "Quantity", "Money");
        var group = table.GetProperty("groups")[0];
        group.GetProperty("subtitle").GetString().Should().NotBeNullOrEmpty("the role");
        group.GetProperty("rows")[0].GetProperty("ticketId").GetGuid().Should().NotBeEmpty();

        var xlsx = await admin.PostAsJsonAsync("/api/admin/reports/sales-by-user/export?format=xlsx", new { });
        xlsx.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using (var book = new XLWorkbook(await xlsx.Content.ReadAsStreamAsync()))
            book.Worksheet(1).Cell(1, 1).GetString().Should().Be("Sales by user");
        var csv = await admin.PostAsJsonAsync("/api/admin/reports/sales-by-user/export?format=csv", new { });
        (await csv.Content.ReadAsStringAsync()).TrimStart('﻿').Should().StartWith("Group,Ticket,Time,Item,Qty,Amount");
        var pdf = await admin.PostAsJsonAsync("/api/admin/reports/sales-by-user/print?target=pdf", new { });
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");

        var printer = app.Db(db => db.Printers.First(p => p.Active).Id);
        var before = app.Db(db => db.PrintJobs.Count(j => j.Label == "Sales by user"));
        (await admin.PostAsJsonAsync($"/api/admin/reports/sales-by-user/print?target=printer&printerId={printer}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        app.Db(db => db.PrintJobs.Count(j => j.Label == "Sales by user")).Should().Be(before + 1);
        (await admin.PostAsJsonAsync($"/api/admin/reports/vat/print?target=printer&printerId={printer}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the VAT summary prints on A4 only");

        (await admin.PostAsJsonAsync("/api/admin/reports/nothing/run", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Every_report_runs_on_the_demo_data_with_a_date_range()
    {
        var admin = await app.AdminClient();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var key in ReportService.Order)
        {
            var result = await Ok(await admin.PostAsJsonAsync($"/api/admin/reports/{key}/run",
                new { from = today.AddDays(-7), to = today.AddDays(1) }));
            result.GetProperty("key").GetString().Should().Be(key);
        }
        var bad = await admin.PostAsJsonAsync("/api/admin/reports/tickets/run", new { from = today, to = today.AddDays(-1) });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Json()).GetProperty("fieldErrors").GetProperty("to").GetString().Should().Contain("before");
    }

    [Fact]
    public async Task The_ticket_detail_shows_a_demo_ticket_and_prints_to_pdf()
    {
        var id = await Ticket();
        var admin = await app.AdminClient();
        var detail = await Ok(await admin.GetAsync($"/api/admin/tickets/{id}"));
        detail.GetProperty("ticket").GetProperty("id").GetGuid().Should().Be(id);
        detail.GetProperty("lines").GetArrayLength().Should().BeGreaterThan(0);
        detail.GetProperty("timeline")[0].GetProperty("kind").GetString().Should().Be("Opened");
        (await admin.GetAsync($"/api/admin/tickets/{id}/pdf")).Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await admin.GetAsync($"/api/admin/tickets/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Only_reports_all_gets_reports_and_the_audit_log_needs_permissions_manage()
    {
        var waiter = await app.PairTerminal();
        (await waiter.LoginWithPin(app.Pins["edna"])).EnsureSuccessStatusCode();
        (await waiter.Http.GetAsync("/api/admin/reports")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await waiter.Http.GetAsync("/api/admin/reports")).Json()).GetArrayLength().Should().Be(0, "reports.own gives nothing here");
        (await waiter.Http.PostAsJsonAsync("/api/admin/reports/sales-by-user/run", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await waiter.Http.PostAsync("/api/pos/reports/sales-by-user/print", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A supervisor prints the Sales by user slip at Close day on their terminal's receipt printer.
        var supervisor = await app.PairTerminal();
        var printer = app.Db(db => db.Printers.Single(p => p.Name == "Ticket Printer 1").Id);
        app.Db(db => db.Terminals.Where(t => t.Id == supervisor.TerminalId).ExecuteUpdate(u => u.SetProperty(t => t.ReceiptPrinterId, printer)));
        (await supervisor.LoginWithPin(app.Pins["MAU"])).EnsureSuccessStatusCode();
        (await supervisor.Http.PostAsync("/api/pos/reports/sales-by-user/print", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        app.Db(db => db.PrintJobs.Any(j => j.PrinterId == printer && j.Label == "Sales by user")).Should().BeTrue();
        (await supervisor.Http.PostAsJsonAsync("/api/admin/reports/audit/run", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
