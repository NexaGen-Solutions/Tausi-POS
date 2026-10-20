using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Tausi.Application.Auditing;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(AppCollection.Name)]
public class SettingsTests(TausiApp app)
{
    private static object Update(JsonElement s, string name) => new
    {
        name,
        address = s.GetProperty("address").GetString(),
        phone = s.GetProperty("phone").GetString(),
        vatRate = s.GetProperty("vatRate").GetDecimal(),
        currency = s.GetProperty("currency").GetString(),
        rounding = s.GetProperty("rounding").GetString(),
        receiptFooter = s.GetProperty("receiptFooter").GetString(),
        printMode = s.GetProperty("printMode").GetString(),
        otherJson = s.GetProperty("otherJson").GetString(),
        rowVersion = s.GetProperty("rowVersion").GetInt64(),
    };

    [Fact]
    public async Task Admin_updates_settings_and_the_change_is_audited_and_announced()
    {
        var admin = await app.AdminClient();
        var settings = await (await admin.GetAsync("/api/settings/business")).Json();
        settings.GetProperty("vatRate").GetDecimal().Should().Be(16m);
        settings.GetProperty("printMode").GetString().Should().Be("OneButton");

        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["edna"]);
        var changed = new TaskCompletionSource();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, $"/hubs/pos?device_token={terminal.DeviceToken}"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult(terminal.SessionToken);
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
        hub.On<JsonElement>("settings.changed", _ => changed.TrySetResult());
        await hub.StartAsync();

        var response = await admin.PutAsJsonAsync("/api/settings/business", Update(settings, "Mau's Village Bar"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Json()).GetProperty("name").GetString().Should().Be("Mau's Village Bar");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.SettingsChanged)).Should().BeGreaterThan(0);

        var stale = await admin.PutAsJsonAsync("/api/settings/business", Update(settings, "Lost update"));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var fresh = await (await admin.GetAsync("/api/settings/business")).Json();
        (await admin.PutAsJsonAsync("/api/settings/business", Update(fresh, "Mau's Village"))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_powered_by_footer_setting_must_be_true_or_false()
    {
        var admin = await app.AdminClient();
        var settings = await (await admin.GetAsync("/api/settings/business")).Json();
        var update = Update(settings, settings.GetProperty("name").GetString()!);
        var bad = JsonSerializer.SerializeToElement(update).Deserialize<Dictionary<string, object?>>()!;
        bad["otherJson"] = """{"print":{"poweredByFooter":"yes"}}""";

        var response = await admin.PutAsJsonAsync("/api/settings/business", bad);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("print.poweredByFooter must be true or false.");
    }

    [Fact]
    public async Task Logo_upload_is_served_and_shows_in_the_brand()
    {
        var admin = await app.AdminClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "logo.png");

        var upload = await admin.PostAsync("/api/settings/business/logo", form);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);
        var logoId = (await upload.Json()).GetProperty("logoFileId").GetGuid();

        var brand = await (await app.CreateClient().GetAsync("/api/brand")).Json();
        brand.GetProperty("logoFileId").GetGuid().Should().Be(logoId);

        var served = await app.CreateClient().GetAsync($"/files/{logoId}");
        served.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await served.Content.ReadAsByteArrayAsync()).Should().Equal(png);
    }
}
