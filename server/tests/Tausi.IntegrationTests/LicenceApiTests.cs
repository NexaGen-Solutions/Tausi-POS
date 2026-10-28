using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Domain.Settings;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>Past the grace period: pairing and the Menu, Stock, Reports and Settings API wait for a key; selling never does.</summary>
[Collection(AppCollection.Name)]
public class LicenceApiTests(TausiApp app) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (app.Db(db => db.WorkPeriods.Any(w => w.IsOpen))) return;
        var supervisor = await app.PairTerminal();
        await supervisor.LoginWithPin(app.Pins["SAM"]);
        (await supervisor.Http.PostAsync("/api/day/open", null)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_expired_licence_blocks_only_the_listed_pages_and_pairing()
    {
        var edna = await app.PairTerminal();
        var printer = app.Db(db => db.Printers.Single(p => p.Name == "Ticket Printer 1").Id);
        app.Db(db => db.Terminals.Where(t => t.Id == edna.TerminalId)
            .ExecuteUpdate(u => u.SetProperty(t => t.ReceiptPrinterId, printer).SetProperty(t => t.DispensePrinterId, printer)));
        (await edna.LoginWithPin(app.Pins["edna"])).EnsureSuccessStatusCode();
        app.Db(db =>
        {
            db.Licences.Add(new Licence
            {
                Key = "TAUSI-TEST", BusinessName = "Demo Bar", Edition = "Standard", TerminalLimit = 20,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-40), LastSeenAt = DateTimeOffset.UtcNow,
            });
            return db.SaveChanges();
        });
        try
        {
            var admin = await app.AdminClient();
            foreach (var path in new[] { "/api/admin/menu", "/api/admin/stock", "/api/admin/reports", "/api/admin/settings" })
            {
                var refused = await admin.GetAsync(path);
                refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
                (await refused.Json()).GetProperty("code").GetString().Should().Be("licence_expired", path);
            }
            (await admin.GetAsync("/api/admin/staff")).StatusCode.Should().Be(HttpStatusCode.OK, "other pages still work");
            (await (await admin.GetAsync("/api/licence")).Json()).GetProperty("status").GetString().Should().Be("Expired");

            var start = await (await app.CreateClient().PostAsync("/api/terminals/pairing", null)).Json();
            (await admin.PostAsJsonAsync("/api/admin/terminals/pair", new { code = start.GetProperty("code").GetString(), newTerminalName = "One more" }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, "pairing waits for a key");

            // A waiter still orders and prints.
            var punch = app.Db(db => db.MenuItems.Single(i => i.Name == "Pineapple Punch").Id);
            var ticket = await (await edna.Http.PostAsJsonAsync("/api/pos/tickets",
                new { customerName = "Licence", lines = new[] { new { menuItemId = punch, quantity = 1 } } })).Json();
            (await edna.Http.PostAsync($"/api/pos/tickets/{ticket.GetProperty("id").GetGuid()}/print", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            app.Db(db => db.Licences.ExecuteDelete());
        }
    }
}
