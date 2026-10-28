using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using SkiaSharp;
using Tausi.Application.Auditing;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(AppCollection.Name)]
public class SettingsTests(TausiApp app)
{
    private static readonly SemaphoreSlim One = new(1, 1);

    [Fact]
    public async Task Admin_saves_the_changed_keys_and_each_is_audited_and_announced()
    {
        await One.WaitAsync();
        try
        {
            var admin = await app.AdminClient();
            var settings = await (await admin.GetAsync("/api/admin/settings")).Json();
            var version = settings.GetProperty("version").GetInt64();
            settings.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("key").GetString())
                .Should().ContainInOrder("business", "rules", "pos", "printing", "money", "cashup");
            (await admin.Setting("business.vatRate")).GetDecimal().Should().Be(16m);
            (await admin.Setting("print.mode")).GetString().Should().Be("OneButton");

            var terminal = await app.PairTerminal();
            await terminal.LoginWithPin(app.Pins["edna"]);
            var changed = new TaskCompletionSource<JsonElement>();
            await using var hub = new HubConnectionBuilder()
                .WithUrl(new Uri(app.Server.BaseAddress, $"/hubs/pos?device_token={terminal.DeviceToken}"), o =>
                {
                    o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                    o.AccessTokenProvider = () => Task.FromResult(terminal.SessionToken);
                    o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                })
                .Build();
            hub.On<JsonElement>("settings.changed", payload => changed.TrySetResult(payload));
            await hub.StartAsync();
            var audits = app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.SettingsChanged));

            var response = await admin.PutAsJsonAsync("/api/admin/settings", new
            {
                values = new Dictionary<string, object> { ["business.name"] = "Demo Bar Two", ["pos.tableIdleMinutes"] = 45, ["pos.logoutAfterSettle"] = false },
                version,
            });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await response.Json()).GetProperty("version").GetInt64().Should().BeGreaterThan(version);

            var payload = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            payload.GetProperty("keys").EnumerateArray().Select(k => k.GetString())
                .Should().BeEquivalentTo("business.name", "pos.tableIdleMinutes"); // logoutAfterSettle was already off
            app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.SettingsChanged)).Should().Be(audits + 2);
            (await (await app.CreateClient().GetAsync("/api/brand")).Json()).GetProperty("name").GetString().Should().Be("Demo Bar Two");

            var history = await (await admin.GetAsync("/api/admin/settings/history")).Json();
            var latest = history.EnumerateArray().First(h => h.GetProperty("key").GetString() == "business.name");
            latest.GetProperty("new").GetString().Should().Be("Demo Bar Two");
            latest.GetProperty("who").GetString().Should().Be("Administrator");

            var stale = await admin.PutAsJsonAsync("/api/admin/settings", new { values = new { pos_x = 1 }, version });
            stale.StatusCode.Should().Be(HttpStatusCode.BadRequest, "unknown keys are refused before anything else");
            var conflict = await admin.PutAsJsonAsync("/api/admin/settings",
                new { values = new Dictionary<string, object> { ["business.name"] = "Lost update" }, version });
            conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await conflict.Json()).GetProperty("detail").GetString().Should().Be("Someone else saved settings. Reload to see their changes.");

            await admin.SaveSettings(new() { ["business.name"] = "Demo Bar", ["pos.tableIdleMinutes"] = 30 });
        }
        finally
        {
            One.Release();
        }
    }

    [Fact]
    public async Task Bad_values_and_unknown_keys_come_back_per_field()
    {
        var admin = await app.AdminClient();
        (await admin.Setting("business.timeZone")).GetString().Should().Be("Africa/Nairobi");

        var bad = await admin.SaveSettings(new()
        {
            ["business.timeZone"] = "Mars/Olympus",
            ["pos.idleLogoutMinutes"] = 500,
            ["print.poweredByFooter"] = "yes",
            ["business.logo"] = "x",
            ["nope.key"] = 1,
        });

        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await bad.Json()).GetProperty("fieldErrors");
        errors.GetProperty("business.timeZone").GetString().Should().Contain("Africa/Nairobi");
        errors.GetProperty("pos.idleLogoutMinutes").GetString().Should().Be("Enter a whole number from 1 to 120.");
        errors.GetProperty("print.poweredByFooter").GetString().Should().Be("Choose on or off.");
        errors.GetProperty("business.logo").GetString().Should().NotBeEmpty();
        errors.GetProperty("nope.key").GetString().Should().Be("Unknown setting.");
    }

    [Fact]
    public async Task A_waiter_can_not_see_settings()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["edna"]);
        (await terminal.Http.GetAsync("/api/admin/settings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_receipt_preview_uses_unsaved_values()
    {
        var admin = await app.AdminClient();
        var values = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["business.name"] = "Preview Bar", ["business.receiptFooter"] = "Karibu tena", ["payments.recordBroughtBy"] = false,
        });

        var preview = await (await admin.GetAsync($"/api/admin/settings/receipt-preview?width=32&values={Uri.EscapeDataString(values)}")).Json();

        preview.GetProperty("width").GetInt32().Should().Be(32);
        preview.GetProperty("bill").GetString().Should().Contain("Preview Bar").And.Contain("Karibu tena");
        preview.GetProperty("receipt").GetString().Should().Contain("RECEIPT No. 2041").And.NotContain("brought by");
        (await admin.Setting("business.name")).GetString().Should().NotBe("Preview Bar", "nothing was saved");

        var posted = await admin.PostAsJsonAsync("/api/admin/settings/receipt-preview", new { width = 42, values = new Dictionary<string, object> { ["pos.idleLogoutMinutes"] = 0 } });
        posted.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Black);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static MultipartFormDataContent Form(byte[] bytes, string type, string name)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        return form;
    }

    [Fact]
    public async Task A_logo_is_checked_kept_with_a_screen_copy_printed_in_1_bit_and_removed()
    {
        await One.WaitAsync();
        try
        {
            var admin = await app.AdminClient();
            (await admin.PostAsync("/api/admin/settings/logo", Form("GIF89a"u8.ToArray(), "image/gif", "logo.gif")))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await admin.PostAsync("/api/admin/settings/logo", Form("not a picture"u8.ToArray(), "image/png", "logo.png")))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await admin.PostAsync("/api/admin/settings/logo", Form(new byte[2 * 1024 * 1024 + 1], "image/png", "big.png")))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var png = Png(1200, 300);
            var upload = await admin.PostAsync("/api/admin/settings/logo", Form(png, "image/png", "logo.png"));
            upload.StatusCode.Should().Be(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());
            var webId = (await upload.Json()).GetProperty("logoFileId").GetGuid();

            var brand = await (await app.CreateClient().GetAsync("/api/brand")).Json();
            brand.GetProperty("logoFileId").GetGuid().Should().Be(webId);
            var web = SKBitmap.Decode(await (await app.CreateClient().GetAsync($"/files/{webId}")).Content.ReadAsByteArrayAsync());
            web.Width.Should().Be(512, "the screen copy is resized");
            var original = app.Db(db => db.BusinessSettings.Single().LogoFileId!.Value);
            (await (await app.CreateClient().GetAsync($"/files/{original}")).Content.ReadAsByteArrayAsync()).Should().Equal(png);

            var print = await admin.GetAsync("/api/admin/settings/logo/print.png?paper=58");
            print.StatusCode.Should().Be(HttpStatusCode.OK);
            SKBitmap.Decode(await print.Content.ReadAsByteArrayAsync()).Width.Should().Be(256, "the 58 mm raster is made from the new logo");

            var removed = await (await admin.DeleteAsync("/api/admin/settings/logo")).Json();
            removed.GetProperty("logoFileId").ValueKind.Should().Be(JsonValueKind.Null);
            (await admin.GetAsync("/api/admin/settings/logo/print.png")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.LogoChanged)).Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            One.Release();
        }
    }
}
