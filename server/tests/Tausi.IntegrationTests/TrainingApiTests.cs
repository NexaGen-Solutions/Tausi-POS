using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>Settings → Training mode over HTTP: a training terminal's tickets stay out of the real data.</summary>
[Collection(StaffCollection.Name)]
public class TrainingApiTests(StaffApp app)
{
    [Fact]
    public async Task A_training_terminal_sells_on_its_own_training_day_and_Clear_removes_it()
    {
        var admin = await app.AdminClient();
        var terminal = await app.PairTerminal();
        var status = await (await admin.GetAsync("/api/admin/training")).Json();
        var row = status.GetProperty("terminals").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == terminal.TerminalId);
        (await admin.PutAsJsonAsync($"/api/admin/training/terminals/{terminal.TerminalId}",
            new { training = true, rowVersion = row.GetProperty("rowVersion").GetInt64() })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Signing in opens the training day: a waiter can start practising at once.
        (await terminal.LoginWithPin(app.Pins["edna"])).EnsureSuccessStatusCode();
        (await (await terminal.Http.GetAsync("/api/device")).Json()).GetProperty("training").GetBoolean().Should().BeTrue();
        var item = app.Db(db => db.MenuItems.Single(i => i.Name == "Pineapple Punch").Id);
        var table = app.Db(db => db.Tables.Single(t => t.Name == "13").Id);
        var created = await terminal.Http.PostAsJsonAsync("/api/pos/tickets", new
        {
            tableId = table, lines = new[] { new { menuItemId = item, quantity = 1, optionIds = Array.Empty<Guid>() } },
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var id = (await created.Json()).GetProperty("id").GetGuid();

        app.Db(db => db.Tickets.Any(t => t.Id == id)).Should().BeFalse("the real side never sees a training ticket");
        app.Db(db => db.Tickets.IgnoreQueryFilters().Single(t => t.Id == id).IsTraining).Should().BeTrue();
        (await (await admin.GetAsync("/api/admin/training")).Json()).GetProperty("tickets").GetInt32().Should().BePositive();

        (await (await admin.PostAsync("/api/admin/training/clear", null)).Json()).GetProperty("tickets").GetInt32().Should().BePositive();
        app.Db(db => db.Tickets.IgnoreQueryFilters().Any(t => t.IsTraining)).Should().BeFalse();
    }
}
