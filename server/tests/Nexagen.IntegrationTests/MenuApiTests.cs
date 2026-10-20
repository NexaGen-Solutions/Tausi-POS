using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Nexagen.IntegrationTests.Support;

namespace Nexagen.IntegrationTests;

[Collection(MenuCollection.Name)]
public class MenuApiTests(MenuApp app)
{
    private Guid ListId(string name) => app.Db(db => db.PriceLists.Single(l => l.Name == name).Id);
    private Guid ItemId(string name) => app.Db(db => db.MenuItems.Single(i => i.Name == name).Id);
    private long ItemVersion(string name) => app.Db(db => db.MenuItems.Single(i => i.Name == name).RowVersion);

    private static decimal PriceOf(JsonElement menu, string name) => menu.GetProperty("categories").EnumerateArray()
        .SelectMany(c => c.GetProperty("items").EnumerateArray())
        .Single(i => i.GetProperty("name").GetString() == name).GetProperty("price").GetDecimal();

    private static MultipartFormDataContent Csv(string csv, object? options = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "menu.csv");
        if (options is not null) form.Add(new StringContent(JsonSerializer.Serialize(options)), "options");
        return form;
    }

    [Fact]
    public async Task The_admin_menu_shows_the_demo_menu_with_both_price_lists()
    {
        var admin = await app.AdminClient();
        var menu = await (await admin.GetAsync("/api/admin/menu")).Json();

        menu.GetProperty("categories").GetArrayLength().Should().Be(6);
        menu.GetProperty("items").GetArrayLength().Should().BeGreaterThanOrEqualTo(13);
        menu.GetProperty("priceLists").EnumerateArray().Select(l => l.GetProperty("name").GetString()).Should().Equal("Standard", "Price 2");
        menu.GetProperty("canEditPrices").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_price_change_reaches_the_terminal_menu_and_the_price_log()
    {
        var admin = await app.AdminClient();
        var counterA = await app.PairTerminal("Menu Counter A");
        var changed = new TaskCompletionSource();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, $"/hubs/pos?device_token={counterA.DeviceToken}"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
        hub.On<JsonElement>("menu.changed", _ => changed.TrySetResult());
        await hub.StartAsync();
        var before = await (await counterA.Http.GetAsync("/api/pos/menu")).Json();

        var put = await admin.PutAsJsonAsync($"/api/admin/menu/items/{ItemId("White Cap Lager")}/prices", new
        {
            prices = new Dictionary<Guid, decimal?> { [ListId("Standard")] = 290m },
            allowZero = false,
            rowVersion = ItemVersion("White Cap Lager"),
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var after = await (await counterA.Http.GetAsync("/api/pos/menu")).Json();
        PriceOf(after, "White Cap Lager").Should().Be(290m);
        after.GetProperty("menuVersion").GetString().Should().NotBe(before.GetProperty("menuVersion").GetString());

        var log = await (await admin.GetAsync($"/api/admin/menu/price-log?itemId={ItemId("White Cap Lager")}")).Json();
        var entry = log.EnumerateArray().First();
        (entry.GetProperty("oldPrice").GetDecimal(), entry.GetProperty("newPrice").GetDecimal()).Should().Be((280m, 290m));
        entry.GetProperty("changedBy").GetString().Should().Be("Administrator");
    }

    [Fact]
    public async Task With_menu_edit_only_a_supervisor_can_rename_but_never_change_a_price()
    {
        var admin = await app.AdminClient();
        var supervisorRole = app.Db(db => db.Roles.Single(r => r.Name == "Supervisor").Id);
        (await admin.PutAsJsonAsync($"/api/admin/roles/{supervisorRole}/permissions", new { permissionKey = "menu.edit", value = "Allowed" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var mau = await app.Db(db => Task.FromResult(db.Users.Single(u => u.Name == "MAU")));
        (await admin.PutAsJsonAsync($"/api/admin/staff/{mau.Id}", new { name = "MAU", roleId = supervisorRole, adminPassword = "mau-password-1", rowVersion = mau.RowVersion }))
            .EnsureSuccessStatusCode();
        var supervisor = await app.AdminClient("mau", "mau-password-1");

        var heineken = app.Db(db => db.MenuItems.Single(i => i.Name == "Heineken"));
        var rename = await supervisor.PutAsJsonAsync($"/api/admin/menu/items/{heineken.Id}", new
        {
            name = "Heineken Bottle", shortName = (string?)null, categoryId = heineken.CategoryId, vatRate = (decimal?)null,
            active = true, erpItemCode = (string?)null, groupIds = (Guid[]?)null, rowVersion = heineken.RowVersion,
        });
        rename.StatusCode.Should().Be(HttpStatusCode.OK);

        var renamed = app.Db(db => db.MenuItems.Single(i => i.Id == heineken.Id));
        (await supervisor.PutAsJsonAsync($"/api/admin/menu/items/{heineken.Id}/prices", new
        {
            prices = new Dictionary<Guid, decimal?> { [ListId("Standard")] = 1m }, allowZero = false, rowVersion = renamed.RowVersion,
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A crafted request putting a price on the item endpoint is refused, even unchanged.
        var crafted = await supervisor.PutAsJsonAsync($"/api/admin/menu/items/{heineken.Id}", new
        {
            name = "Heineken Bottle", shortName = (string?)null, categoryId = heineken.CategoryId, vatRate = (decimal?)null,
            active = true, erpItemCode = (string?)null, groupIds = (Guid[]?)null, rowVersion = renamed.RowVersion,
            prices = new Dictionary<Guid, decimal> { [ListId("Standard")] = 300m },
        });
        crafted.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        app.Db(db => db.MenuItemPrices.Single(p => p.MenuItemId == heineken.Id).Price).Should().Be(300m);

        (await supervisor.PutAsJsonAsync($"/api/admin/roles/{supervisorRole}/permissions", new { permissionKey = "menu.edit_prices", value = "Allowed" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "only permissions.manage may change the grid");
    }

    [Fact]
    public async Task A_terminal_on_price_2_sells_price_2_where_set_and_standard_elsewhere()
    {
        var admin = await app.AdminClient();
        var counterB = await app.PairTerminal("Menu Counter B");
        (await admin.PutAsJsonAsync($"/api/admin/menu/items/{ItemId("Balozi Lager")}/prices", new
        {
            prices = new Dictionary<Guid, decimal?> { [ListId("Price 2")] = 270m }, allowZero = false, rowVersion = ItemVersion("Balozi Lager"),
        })).EnsureSuccessStatusCode();

        var terminals = await (await admin.GetAsync("/api/admin/terminals")).Json();
        var listed = terminals.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Menu Counter B");
        listed.GetProperty("id").GetGuid().Should().Be(counterB.TerminalId);

        (await admin.PutAsJsonAsync($"/api/admin/terminals/{counterB.TerminalId}/price-list", new { priceListId = ListId("Price 2") }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await admin.GetAsync("/api/admin/terminals")).Json()).EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "Menu Counter B").GetProperty("priceListId").GetGuid().Should().Be(ListId("Price 2"));

        var menu = await (await counterB.Http.GetAsync("/api/pos/menu")).Json();
        menu.GetProperty("priceListName").GetString().Should().Be("Price 2");
        PriceOf(menu, "Balozi Lager").Should().Be(270m);
        PriceOf(menu, "Faxe Can").Should().Be(175m);
    }

    [Fact]
    public async Task Csv_export_and_import_round_trip_over_http()
    {
        var admin = await app.AdminClient();
        var export = await admin.GetAsync("/api/admin/menu/export.csv");
        export.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var csv = await export.Content.ReadAsStringAsync();

        var preview = await (await admin.PostAsync("/api/admin/menu/import/preview", Csv(csv))).Json();
        preview.GetProperty("new").GetInt32().Should().Be(0);
        preview.GetProperty("updated").GetInt32().Should().Be(0);
        preview.GetProperty("errors").GetInt32().Should().Be(0);

        var added = csv.TrimEnd() + "\r\nFood,Mandazi,Mandazi,,yes,50,,,\r\n";
        var apply = await admin.PostAsync("/api/admin/menu/import/apply", Csv(added, new { updateExisting = true, deactivateMissing = false, skipErrors = false }));
        apply.StatusCode.Should().Be(HttpStatusCode.OK);
        (await apply.Json()).GetProperty("new").GetInt32().Should().Be(1);
        app.Db(db => db.PriceChangeLogs.Any(l => l.Source == "import")).Should().BeTrue();
    }

    [Fact]
    public async Task Waiters_cannot_open_the_menu_admin()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["Ruth"]);
        (await terminal.Http.GetAsync("/api/admin/menu")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
