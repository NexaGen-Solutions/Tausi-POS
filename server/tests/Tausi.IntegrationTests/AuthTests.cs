using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Application.Auditing;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(AppCollection.Name)]
public class AuthTests(TausiApp app)
{
    [Fact]
    public async Task Paired_terminal_knows_its_name_and_the_business()
    {
        var terminal = await app.PairTerminal();

        var device = await (await terminal.Http.GetAsync("/api/device")).Json();

        device.GetProperty("terminalName").GetString().Should().Be(terminal.Name);
        device.GetProperty("businessName").GetString().Should().Be("Demo Bar");
    }

    [Fact]
    public async Task Unknown_device_token_is_not_a_terminal()
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Token", "made-up");

        (await client.GetAsync("/api/device")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pin_login_gives_a_session_on_that_terminal()
    {
        var terminal = await app.PairTerminal();

        var login = await terminal.LoginWithPin(app.Pins["edna"]);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await (await terminal.Http.GetAsync("/api/auth/me")).Json();
        me.GetProperty("user").GetProperty("name").GetString().Should().Be("edna");
        me.GetProperty("user").GetProperty("terminalName").GetString().Should().Be(terminal.Name);
        me.GetProperty("permissions").GetProperty("order.void").GetString().Should().Be("NeedsApproval");
    }

    [Fact]
    public async Task Session_only_works_with_its_own_terminal()
    {
        var counter = await app.PairTerminal();
        await counter.LoginWithPin(app.Pins["CAROL"]);
        var other = await app.PairTerminal();

        var noDevice = app.CreateClient();
        noDevice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", counter.SessionToken);
        (await noDevice.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        other.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", counter.SessionToken);
        (await other.Http.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_wrong_pins_lock_the_terminal_and_the_lock_is_audited()
    {
        var terminal = await app.PairTerminal();

        for (var i = 1; i <= 4; i++)
        {
            var wrong = await terminal.LoginWithPin("0000");
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await wrong.Json()).GetProperty("attemptsLeft").GetInt32().Should().Be(5 - i);
        }
        var locked = await terminal.LoginWithPin("0000");
        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        locked.Headers.RetryAfter!.Delta.Should().BeCloseTo(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2));

        (await terminal.LoginWithPin(app.Pins["edna"])).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.TerminalLocked && a.TerminalId == terminal.TerminalId))
            .Should().Be(1);
    }

    [Fact]
    public async Task Admin_site_is_for_admin_and_supervisor_roles_only()
    {
        var client = app.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/login", new { userName = "admin", password = "wrong" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/admin/login", new { userName = "admin", password = TausiApp.AdminPassword }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logins_are_audited()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["Ruth"]);

        app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.PinLogin && a.TerminalId == terminal.TerminalId))
            .Should().Be(1);
    }
}
