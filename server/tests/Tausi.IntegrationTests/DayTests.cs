using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(DayCollection.Name)]
public class DayTests(DayApp app)
{
    [Fact]
    public async Task Opening_the_day_needs_permission_happens_once_and_reaches_login_screens()
    {
        // A terminal sitting on the PIN screen: device token only, nobody logged in.
        var idle = await app.PairTerminal();
        var opened = new TaskCompletionSource<JsonElement>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, $"/hubs/pos?device_token={idle.DeviceToken}"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
        hub.On<JsonElement>("day.opened", payload => opened.TrySetResult(payload));
        await hub.StartAsync();

        var device = await (await idle.Http.GetAsync("/api/device")).Json();
        device.GetProperty("workPeriod").ValueKind.Should().Be(JsonValueKind.Null);

        var waiter = await app.PairTerminal();
        await waiter.LoginWithPin(app.Pins["edna"]);
        (await waiter.Http.PostAsync("/api/day/open", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var supervisor = await app.PairTerminal();
        await supervisor.LoginWithPin(app.Pins["MAU"]);
        var open = await supervisor.Http.PostAsync("/api/day/open", null);
        open.StatusCode.Should().Be(HttpStatusCode.OK);
        var periodId = (await open.Json()).GetProperty("id").GetGuid();

        (await opened.Task.WaitAsync(TimeSpan.FromSeconds(10))).GetProperty("workPeriodId").GetGuid().Should().Be(periodId);
        device = await (await idle.Http.GetAsync("/api/device")).Json();
        device.GetProperty("workPeriod").GetProperty("id").GetGuid().Should().Be(periodId);

        (await supervisor.Http.PostAsync("/api/day/open", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_unpaired_device_cannot_listen()
    {
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/pos"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();

        await FluentActions.Awaiting(() => hub.StartAsync()).Should().ThrowAsync<HttpRequestException>();
    }
}
