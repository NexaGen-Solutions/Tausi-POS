using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(StaffCollection.Name)]
public class StaffApiTests(StaffApp app)
{
    private static HttpRequestMessage Post(string url, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return request;
    }

    private Guid RoleId(string name) => app.Db(db => db.Roles.Single(r => r.Name == name).Id);

    private async Task<JsonElement> Member(HttpClient admin, string name)
    {
        var list = await (await admin.GetAsync("/api/admin/staff")).Json();
        return list.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);
    }

    private static async Task<HubConnection> Listen(StaffApp app, TerminalClient terminal, string eventName, TaskCompletionSource<JsonElement> received)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, $"/hubs/pos?device_token={terminal.DeviceToken}"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
        hub.On<JsonElement>(eventName, payload => received.TrySetResult(payload));
        await hub.StartAsync();
        return hub;
    }

    [Fact]
    public async Task Add_grace_who_must_change_her_pin_and_the_pin_is_never_stored_in_clear()
    {
        var admin = await app.AdminClient();
        var key = Guid.NewGuid().ToString();
        var body = new { name = "Grace", roleId = RoleId("Waiter"), startingPin = "6284", adminPassword = (string?)null };

        var created = await admin.SendAsync(Post("/api/admin/staff", body, key));
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        (await created.Json()).GetProperty("temporaryPin").GetString().Should().Be("6284");

        // A retried request gets the original answer, but the PIN is not kept for replays.
        var replay = await admin.SendAsync(Post("/api/admin/staff", body, key));
        replay.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
        (await replay.Json()).GetProperty("temporaryPin").ValueKind.Should().Be(JsonValueKind.Null);
        app.Db(db => db.IdempotencyRecords.Where(r => r.Key == key).Select(r => r.ResponseBody).Single()).Should().NotContain("6284");
        app.Db(db => db.AuditEntries.Where(a => a.Action.StartsWith("staff.")).Select(a => a.NewValueJson).ToList())
            .Should().NotContain(v => v != null && v.Contains("6284"));

        var terminal = await app.PairTerminal();
        var login = await (await terminal.LoginWithPin("6284")).Json();
        login.GetProperty("mustChangePin").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Resetting_a_logged_in_users_pin_ends_their_session_and_tells_their_terminal()
    {
        var admin = await app.AdminClient();
        var counterB = await app.PairTerminal();
        (await counterB.LoginWithPin(app.Pins["edna"])).EnsureSuccessStatusCode();
        var signal = new TaskCompletionSource<JsonElement>();
        await using var hub = await Listen(app, counterB, "user.pin_reset", signal);

        var edna = await Member(admin, "edna");
        var reset = await admin.SendAsync(Post($"/api/admin/staff/{edna.GetProperty("id").GetGuid()}/reset-pin",
            new { newPin = (string?)null, generate = true, rowVersion = edna.GetProperty("rowVersion").GetInt64() }));
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        var temporary = (await reset.Json()).GetProperty("temporaryPin").GetString();

        (await signal.Task.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("userId").GetGuid().Should().Be(edna.GetProperty("id").GetGuid());
        var old = await counterB.Http.GetAsync("/api/auth/session");
        old.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await old.Json()).GetProperty("code").GetString().Should().Be("session_expired");

        var fresh = await app.PairTerminal();
        (await fresh.LoginWithPin(app.Pins["edna"])).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await (await fresh.LoginWithPin(temporary!)).Json()).GetProperty("mustChangePin").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Deactivating_someone_logs_them_out_everywhere_and_keeps_their_name()
    {
        var admin = await app.AdminClient();
        await admin.SendAsync(Post("/api/admin/staff", new { name = "Wairimu", roleId = RoleId("Waiter"), startingPin = "7146" }));
        var terminal = await app.PairTerminal();
        (await terminal.LoginWithPin("7146")).EnsureSuccessStatusCode();
        var signal = new TaskCompletionSource<JsonElement>();
        await using var hub = await Listen(app, terminal, "user.deactivated", signal);

        var wairimu = await Member(admin, "Wairimu");
        var id = wairimu.GetProperty("id").GetGuid();
        (await admin.SendAsync(Post($"/api/admin/staff/{id}/deactivate", new { rowVersion = wairimu.GetProperty("rowVersion").GetInt64() })))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await signal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        (await terminal.Http.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await (await app.PairTerminal()).LoginWithPin("7146")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        app.Db(db => db.AuditEntries.Count(a => a.UserId == id)).Should().BeGreaterThan(0, "their history stays linked");

        var again = await admin.SendAsync(Post("/api/admin/staff", new { name = "WAIRIMU", roleId = RoleId("Waiter"), startingPin = "8352" }));
        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await again.Json()).GetProperty("code").GetString().Should().Be("name_taken");
    }

    [Fact]
    public async Task A_supervisor_on_the_admin_site_only_manages_waiters_and_cashiers()
    {
        var admin = await app.AdminClient();
        var mau = await Member(admin, "MAU");
        (await admin.PutAsJsonAsync($"/api/admin/staff/{mau.GetProperty("id").GetGuid()}", new
        {
            name = "MAU", roleId = RoleId("Supervisor"), adminPassword = "mau-password-1", rowVersion = mau.GetProperty("rowVersion").GetInt64(),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var supervisor = await app.AdminClient("mau", "mau-password-1");
        var list = await (await supervisor.GetAsync("/api/admin/staff")).Json();
        list.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("roleName").GetString())
            .Should().OnlyContain(r => r == "Waiter" || r == "Cashier");
        list.GetProperty("assignableRoles").EnumerateArray().Select(r => r.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["Cashier", "Waiter"]);

        var adminId = app.Db(db => db.Users.Single(u => u.Name == "Administrator").Id);
        (await supervisor.PutAsJsonAsync($"/api/admin/staff/{adminId}", new { name = "X", roleId = RoleId("Waiter"), rowVersion = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        var promote = await supervisor.SendAsync(Post("/api/admin/staff",
            new { name = "Boss", roleId = RoleId("Admin"), startingPin = "5937", adminPassword = "a-long-password" }));
        promote.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await promote.Json()).GetProperty("code").GetString().Should().Be("role_not_assignable");
    }

    [Fact]
    public async Task The_last_admin_cannot_be_deactivated_and_an_admin_cannot_demote_themselves()
    {
        var admin = await app.AdminClient();
        var me = await Member(admin, "Administrator");
        var id = me.GetProperty("id").GetGuid();

        var deactivate = await admin.SendAsync(Post($"/api/admin/staff/{id}/deactivate", new { rowVersion = me.GetProperty("rowVersion").GetInt64() }));
        deactivate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await deactivate.Json()).GetProperty("detail").GetString().Should().Be("There must be at least one active Admin.");

        var demote = await admin.PutAsJsonAsync($"/api/admin/staff/{id}", new
        {
            name = "Administrator", roleId = RoleId("Waiter"), rowVersion = me.GetProperty("rowVersion").GetInt64(),
        });
        demote.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await demote.Json()).GetProperty("code").GetString().Should().Be("own_role");
    }

    [Fact]
    public async Task Someone_else_saving_first_gives_409()
    {
        var admin = await app.AdminClient();
        var ruth = await Member(admin, "Ruth");
        var id = ruth.GetProperty("id").GetGuid();
        var version = ruth.GetProperty("rowVersion").GetInt64();

        (await admin.PutAsJsonAsync($"/api/admin/staff/{id}", new { name = "Ruth", roleId = RoleId("Cashier"), rowVersion = version }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await admin.PutAsJsonAsync($"/api/admin/staff/{id}", new { name = "Ruth W", roleId = RoleId("Waiter"), rowVersion = version });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Json()).GetProperty("detail").GetString().Should().Be("This person was changed by someone else. Reload and try again.");
    }

    [Fact]
    public async Task Staff_pages_need_staff_manage()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["CYNTHIA"]);

        (await terminal.Http.GetAsync("/api/admin/staff")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
