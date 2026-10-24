using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Domain.Stock;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>Stock over HTTP (PostgreSQL): selling under load, deliveries, waste and the count sheet.</summary>
[Collection(TicketsCollection.Name)]
public class StockApiTests(TicketsApp app) : IAsyncLifetime
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

    private async Task<TerminalClient> On(string name)
    {
        var terminal = await app.PairTerminal();
        var printer = app.Db(db => db.Printers.Single(p => p.Name == "Ticket Printer 1").Id);
        app.Db(db => db.Terminals.Where(t => t.Id == terminal.TerminalId)
            .ExecuteUpdate(u => u.SetProperty(t => t.ReceiptPrinterId, printer).SetProperty(t => t.DispensePrinterId, printer)));
        (await terminal.LoginWithPin(app.Pins[name])).EnsureSuccessStatusCode();
        return terminal;
    }

    private Guid StockItem(string name) => app.Db(db => db.StockItems.Single(i => i.Name == name).Id);
    private Guid Location(string name) => app.Db(db => db.StockLocations.Single(l => l.Name == name).Id);

    private decimal OnHand(string item, string location)
    {
        var (i, l) = (StockItem(item), Location(location));
        return app.Db(db => db.StockLevels.Single(x => x.StockItemId == i && x.LocationId == l).OnHand);
    }

    private decimal MovementSum(string item, string location)
    {
        var (i, l) = (StockItem(item), Location(location));
        return app.Db(db => db.StockMovements.Where(m => m.StockItemId == i && m.LocationId == l).Sum(m => m.Quantity));
    }

    [Fact]
    public async Task Concurrent_prints_of_two_tickets_with_the_same_item_keep_on_hand_consistent()
    {
        var terminals = new[] { await On("edna"), await On("Ruth"), await On("CYNTHIA"), await On("WAMBOO") };
        var heineken = app.Db(db => db.MenuItems.Single(m => m.Name == "Heineken").Id);
        var cold = app.Db(db => db.ModifierOptions.Single(o => o.Name == "Cold").Id);
        var tickets = new List<(TerminalClient Terminal, Guid Id)>();
        foreach (var t in terminals)
        {
            var ticket = await (await t.Http.PostAsJsonAsync("/api/pos/tickets",
                new { lines = new[] { new { menuItemId = heineken, quantity = 3, optionIds = new[] { cold } } } })).Json();
            tickets.Add((t, ticket.GetProperty("id").GetGuid()));
        }
        var before = OnHand("Heineken", "Bar");

        var printed = await Task.WhenAll(tickets.Select(x => x.Terminal.Http.PostAsync($"/api/pos/tickets/{x.Id}/print", null)));

        printed.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
        OnHand("Heineken", "Bar").Should().Be(before - 12);
        OnHand("Heineken", "Bar").Should().Be(MovementSum("Heineken", "Bar"), "the cached on hand equals the movements");
    }

    [Fact]
    public async Task A_delivery_waste_and_the_count_sheet_over_the_api()
    {
        var admin = await app.AdminClient();
        var kbl = app.Db(db => db.Suppliers.Single(s => s.Name == "KBL").Id);
        var invoice = $"API-{Guid.NewGuid():N}"[..12];
        var store = OnHand("Tusker Lager", "Store");

        var saved = await admin.PostAsJsonAsync("/api/admin/deliveries", new
        {
            supplierId = kbl, invoiceNumber = invoice, locationId = Location("Store"), payment = "OnCredit",
            lines = new[] { new { stockItemId = StockItem("Tusker Lager"), unit = "crate", quantity = 4, unitCost = 2500 } },
        });
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        OnHand("Tusker Lager", "Store").Should().Be(store + 100);
        var again = await admin.PostAsJsonAsync("/api/admin/deliveries", new
        {
            supplierId = kbl, invoiceNumber = invoice, locationId = Location("Store"), payment = "OnCredit",
            lines = new[] { new { stockItemId = StockItem("Tusker Lager"), unit = "crate", quantity = 1, unitCost = 2500 } },
        });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Json()).GetProperty("detail").GetString().Should().StartWith($"Invoice {invoice} from KBL is already recorded on");

        var waste = await admin.PostAsJsonAsync("/api/admin/stock/waste",
            new { locationId = Location("Bar"), stockItemId = StockItem("Guiness"), quantity = 1, unit = "bottle", reason = "Broken" });
        waste.StatusCode.Should().Be(HttpStatusCode.OK, await waste.Content.ReadAsStringAsync());

        var overview = await (await admin.GetAsync($"/api/admin/stock?locationId={Location("Bar")}")).Json();
        overview.GetProperty("items").EnumerateArray().Should().Contain(i => i.GetProperty("name").GetString() == "Tusker Lager");
        overview.GetProperty("noRecipe").EnumerateArray().Should().Contain(n => n.GetProperty("name").GetString() == "Pool Table");

        var countClient = admin;
        var count = await (await countClient.PostAsJsonAsync("/api/admin/stock-counts",
            new { locationId = Location("Store"), kind = "Partial", isOpening = false, itemIds = new[] { StockItem("Delmonte") } })).Json();
        var id = count.GetProperty("id").GetGuid();
        count.GetProperty("lines")[0].GetProperty("expected").ValueKind.Should().Be(JsonValueKind.Null, "blind");
        var pdf = await countClient.GetAsync($"/api/admin/stock-counts/{id}/sheet.pdf");
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        System.Text.Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync()).Should().Contain("Delmonte").And.NotContain("Expected");
        app.Db(db => db.JournalEntries.Include(e => e.Lines).ToList())
            .Should().OnlyContain(e => e.Lines.Sum(l => l.Debit) == e.Lines.Sum(l => l.Credit));
    }
}
