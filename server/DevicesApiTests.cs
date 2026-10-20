using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(DevicesCollection.Name)]
public class DevicesApiTests(DevicesApp app)
{
    private HubConnection Hub(TerminalClient terminal) => new HubConnectionBuilder()
        .WithUrl(new Uri(app.Server.BaseAddress, $"/hubs/pos?device_token={terminal.DeviceToken}"), o =>
        {
            o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
        })
        .Build();

    private static JsonElement Row(JsonElement list, string name) =>
        list.EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);

    [Fact]
    public async Task The_old_dev_pairing_endpoint_is_gone()
    {
        var admin = await app.AdminClient();
        (await admin.PostAsJsonAsync("/api/terminals/pair", new { code = "123456", terminalName = "X" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_paired_terminal_shows_online_with_its_user_and_revoking_sends_it_back_to_pairing()
    {
        var admin = await app.AdminClient();
        var terminal = await app.PairTerminal("Revoke Me");
        var revoked = new TaskCompletionSource<JsonElement>();
        await using var hub = Hub(terminal);
        hub.On<JsonElement>("terminal.revoked", p => revoked.TrySetResult(p));
        await hub.StartAsync();
        (await terminal.LoginWithPin(app.Pins["edna"])).EnsureSuccessStatusCode();

        var row = Row(await (await admin.GetAsync("/api/admin/terminals")).Json(), "Revoke Me");
        row.GetProperty("status").GetString().Should().Be("Online");
        row.GetProperty("currentUser").GetString().Should().Be("edna");

        (await admin.PostAsync($"/api/admin/terminals/{terminal.TerminalId}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await revoked.Task.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("terminalId").GetGuid().Should().Be(terminal.TerminalId);
        (await terminal.Http.GetAsync("/api/device")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Row(await (await admin.GetAsync("/api/admin/terminals")).Json(), "Revoke Me").GetProperty("status").GetString().Should().Be("Revoked");
    }

    [Fact]
    public async Task Wrong_codes_say_so_and_then_block()
    {
        var admin = await app.AdminClient();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++)
            last = await admin.PostAsJsonAsync("/api/admin/terminals/pair", new { code = "999999", newTerminalName = "Nope" });
        last.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var other = await app.AdminClient();
        var wrong = await other.PostAsJsonAsync("/api/admin/terminals/pair", new { code = "999999", newTerminalName = "Nope" });
        // Same admin user, so still blocked.
        wrong.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_dead_network_printer_explains_itself_and_its_failed_test_print_can_go_elsewhere()
    {
        var admin = await app.AdminClient();
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var check = await (await admin.PostAsJsonAsync("/api/admin/printers/check", new { connectionMode = "Network9100", address = $"127.0.0.1:{port}" })).Json();
        check.GetProperty("reachable").GetBoolean().Should().BeFalse();
        check.GetProperty("message").GetString().Should().StartWith($"127.0.0.1:{port} refused the connection");

        var created = await admin.PostAsJsonAsync("/api/admin/printers", new
        {
            name = "Dead Network", connectionMode = "Network9100", address = $"127.0.0.1:{port}", owningTerminalId = (Guid?)null,
            paperWidthMm = 80, cutAfterSlip = true, openDrawerOnReceipt = false, active = true,
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var dead = (await created.Json()).GetProperty("id").GetGuid();

        var job = (await (await admin.PostAsync($"/api/admin/printers/{dead}/test", null)).Json()).GetProperty("printJobId").GetGuid();
        JsonElement failed = default;
        for (var i = 0; i < 100; i++)
        {
            failed = await (await admin.GetAsync($"/api/admin/printers/{dead}/jobs?status=Failed")).Json();
            if (failed.GetArrayLength() > 0) break;
            await Task.Delay(300);
        }
        failed.GetArrayLength().Should().Be(1, "4 tries with 2, 4 and 8 s waits");
        failed[0].GetProperty("error").GetString().Should().Contain("refused the connection");

        var filePrinter = app.Db(db => db.Printers.Single(p => p.Name == "Ticket Printer 2").Id);
        (await admin.PostAsJsonAsync($"/api/admin/print-jobs/{job}/redirect", new { printerId = filePrinter })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        string? slip = null;
        for (var i = 0; i < 50 && slip is null; i++)
        {
            await Task.Delay(200);
            if (Directory.Exists(app.PrintOutput))
                slip = Directory.GetFiles(app.PrintOutput, $"*{job.ToString("N")[..8]}*").FirstOrDefault();
        }
        slip.Should().NotBeNull();
        (await File.ReadAllTextAsync(slip!)).Should().Contain("Dead Network").And.Contain("Test OK");
    }

    [Fact]
    public async Task Printers_used_by_terminals_cannot_be_deactivated()
    {
        var admin = await app.AdminClient();
        var tp1 = Row(await (await admin.GetAsync("/api/admin/printers")).Json(), "Ticket Printer 1");
        var response = await admin.PutAsJsonAsync($"/api/admin/printers/{tp1.GetProperty("id").GetGuid()}", new
        {
            name = "Ticket Printer 1", connectionMode = tp1.GetProperty("connectionMode").GetString(), address = tp1.GetProperty("address").GetString(),
            owningTerminalId = (Guid?)null, paperWidthMm = 80, cutAfterSlip = true, openDrawerOnReceipt = false, active = false,
            rowVersion = tp1.GetProperty("rowVersion").GetInt64(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Json()).GetProperty("code").GetString().Should().Be("printer_in_use");
    }
}
