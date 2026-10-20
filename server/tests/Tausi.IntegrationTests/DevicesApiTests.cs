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
        // Its own admin, so the block doesn't stop the other tests pairing.
        var main = await app.AdminClient();
        var adminRole = app.Db(db => db.Roles.Single(r => r.Name == "Admin").Id);
        (await main.PostAsJsonAsync("/api/admin/staff", new { name = "Pairing Tester", roleId = adminRole, startingPin = "5927", adminPassword = "pairing-tester-1" }))
            .EnsureSuccessStatusCode();
        var admin = await app.AdminClient("pairing.tester", "pairing-tester-1");
        var first = await admin.PostAsJsonAsync("/api/admin/terminals/pair", new { code = "999999", newTerminalName = "Nope" });
        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await first.Json()).GetProperty("detail").GetString().Should().Be("Code not found or expired.");

        HttpResponseMessage last = null!;
        for (var i = 0; i < 4; i++)
            last = await admin.PostAsJsonAsync("/api/admin/terminals/pair", new { code = "999999", newTerminalName = "Nope" });
        last.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
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

        var filePrinter = (await (await admin.PostAsJsonAsync("/api/admin/printers", new
        {
            name = "Spare File", connectionMode = "File", address = "", owningTerminalId = (Guid?)null,
            paperWidthMm = 80, cutAfterSlip = true, openDrawerOnReceipt = false, active = true,
        })).Json()).GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync($"/api/admin/print-jobs/{job}/redirect", new { printerId = filePrinter })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        string? slip = null;
        for (var i = 0; i < 50 && slip is null; i++)
        {
            await Task.Delay(200);
            if (Directory.Exists(app.PrintOutput))
                slip = Directory.GetFiles(app.PrintOutput, $"*{job.ToString("N")[^8..]}.txt").FirstOrDefault();
        }
        slip.Should().NotBeNull();
        (await File.ReadAllTextAsync(slip!)).Should().Contain("Dead Network").And.Contain("Test OK");
    }

    [Fact]
    public async Task The_powered_by_setting_shows_on_the_test_print_sample_bill()
    {
        var admin = await app.AdminClient();
        async Task SetPoweredBy(bool on)
        {
            var s = await (await admin.GetAsync("/api/settings/business")).Json();
            var other = System.Text.Json.Nodes.JsonNode.Parse(s.GetProperty("otherJson").GetString() ?? "{}")!.AsObject();
            other["print"] ??= new System.Text.Json.Nodes.JsonObject();
            other["print"]!["poweredByFooter"] = on;
            (await admin.PutAsJsonAsync("/api/settings/business", new
            {
                name = s.GetProperty("name").GetString(),
                address = s.GetProperty("address").GetString(),
                phone = s.GetProperty("phone").GetString(),
                vatRate = s.GetProperty("vatRate").GetDecimal(),
                currency = s.GetProperty("currency").GetString(),
                rounding = s.GetProperty("rounding").GetString(),
                receiptFooter = s.GetProperty("receiptFooter").GetString(),
                printMode = s.GetProperty("printMode").GetString(),
                otherJson = other.ToJsonString(),
                rowVersion = s.GetProperty("rowVersion").GetInt64(),
            })).EnsureSuccessStatusCode();
        }

        var printer = (await (await admin.PostAsJsonAsync("/api/admin/printers", new
        {
            name = "Footer File", connectionMode = "File", address = "", owningTerminalId = (Guid?)null,
            paperWidthMm = 80, cutAfterSlip = true, openDrawerOnReceipt = false, active = true,
        })).Json()).GetProperty("id").GetGuid();
        await SetPoweredBy(true);
        try
        {
            var job = (await (await admin.PostAsync($"/api/admin/printers/{printer}/test", null)).Json()).GetProperty("printJobId").GetGuid();
            string? slip = null;
            for (var i = 0; i < 50 && slip is null; i++)
            {
                await Task.Delay(200);
                if (Directory.Exists(app.PrintOutput))
                    slip = Directory.GetFiles(app.PrintOutput, $"*{job.ToString("N")[^8..]}.txt").FirstOrDefault();
            }
            slip.Should().NotBeNull();
            (await File.ReadAllTextAsync(slip!)).Should().Contain("Powered by Tausi POS");
        }
        finally
        {
            await SetPoweredBy(false);
        }
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
