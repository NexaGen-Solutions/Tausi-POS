using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tausi.Application.Auditing;
using Tausi.Application.Setup;
using Tausi.Application.Updates;
using Tausi.Infrastructure;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>
/// Health, the security headers, admin sign-in limits, the terminals' update source (server first,
/// old terminals update before login), Settings → System, and a lost admin password.
/// </summary>
[Collection(StaffCollection.Name)]
public class SystemApiTests(StaffApp app)
{
    [Fact]
    public async Task Health_says_the_version_database_disk_backup_and_licence()
    {
        var health = await (await app.CreateClient().GetAsync("/health")).Json();
        health.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        health.GetProperty("database").GetString().Should().Be("ok");
        health.GetProperty("diskFreeBytes").GetInt64().Should().BePositive();
        health.GetProperty("backup").GetString().Should().Be("none", "a new server has no backup yet, and isn't 48 hours old");
        health.GetProperty("licence").GetString().Should().NotBeNullOrEmpty();
        health.GetProperty("minimumTerminalVersion").GetString().Should().Be(TerminalVersions.Minimum);
        (await app.CreateClient().GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_answer_carries_the_security_headers()
    {
        foreach (var response in new[] { await app.CreateClient().GetAsync("/api/health"), await app.CreateClient().GetAsync("/api/admin/staff") })
        {
            response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
            response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
            response.Headers.GetValues("Referrer-Policy").Should().Equal("same-origin");
            response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'");
        }
        (await app.CreateClient().GetAsync("/api/health")).Headers.GetValues("Content-Security-Policy").Single()
            .Should().Be("default-src 'none'; frame-ancestors 'none'");
    }

    [Fact]
    public async Task Ten_wrong_admin_passwords_lock_the_user_name()
    {
        var client = app.CreateClient();
        for (var i = 0; i < 9; i++)
            (await client.PostAsJsonAsync("/api/admin/login", new { userName = "nobody-" + i % 2, password = "wrong" }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // The same address: the tenth wrong password locks it, for every name.
        (await client.PostAsJsonAsync("/api/admin/login", new { userName = "nobody-x", password = "wrong" })).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var locked = await client.PostAsJsonAsync("/api/admin/login", new { userName = "admin", password = TausiApp.AdminPassword });
        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        locked.Headers.RetryAfter.Should().NotBeNull();
        app.Services.GetRequiredService<Tausi.Application.Auth.LoginThrottle>().Clear(Tausi.Application.Auth.LoginThrottle.IpKey(null));
        foreach (var ip in new[] { "127.0.0.1", "::1", "unknown" })
            app.Services.GetRequiredService<Tausi.Application.Auth.LoginThrottle>().Clear(Tausi.Application.Auth.LoginThrottle.IpKey(ip));
    }

    private string TerminalFolder => Path.GetFullPath(app.Services.GetRequiredService<IOptions<TausiOptions>>().Value.Updates.TerminalFolder);

    [Fact]
    public async Task Terminals_update_from_their_server_never_past_its_version()
    {
        var anon = app.CreateClient();
        Directory.CreateDirectory(TerminalFolder);
        File.Delete(Path.Combine(TerminalFolder, "terminal.json"));
        (await anon.GetAsync("/api/updates/terminal/latest.json")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await File.WriteAllTextAsync(Path.Combine(TerminalFolder, "TausiPOS-Setup.exe"), "installer bytes");
        await File.WriteAllTextAsync(Path.Combine(TerminalFolder, "terminal.json"), JsonSerializer.Serialize(new
        {
            version = "0.0.5", notes = "Faster.", pubDate = DateTimeOffset.UtcNow, file = "TausiPOS-Setup.exe", signature = "dW50cnVzdGVkIGNvbW1lbnQ=",
        }));
        var latest = await (await anon.GetAsync("/api/updates/terminal/latest.json")).Json();
        latest.GetProperty("version").GetString().Should().Be("0.0.5");
        var url = latest.GetProperty("platforms").GetProperty("windows-x86_64").GetProperty("url").GetString()!;
        url.Should().EndWith("/api/updates/terminal/files/TausiPOS-Setup.exe");
        (await anon.GetStringAsync(new Uri(url).PathAndQuery)).Should().Be("installer bytes");

        // A terminal build newer than this server is never offered (server first).
        await File.WriteAllTextAsync(Path.Combine(TerminalFolder, "terminal.json"), JsonSerializer.Serialize(new
        {
            version = "999.0.0", file = "TausiPOS-Setup.exe", signature = "x",
        }));
        (await anon.GetAsync("/api/updates/terminal/latest.json")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await anon.GetAsync("/api/updates/terminal/files/..%2Fterminal.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_terminal_older_than_the_minimum_updates_before_login()
    {
        var terminal = await app.PairTerminal();
        terminal.Http.DefaultRequestHeaders.Add(TerminalVersions.Header, "0.0.1");
        var old = await terminal.LoginWithPin(app.Pins["edna"]);
        old.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired);
        var problem = await old.Json();
        (problem.GetProperty("code").GetString(), problem.GetProperty("detail").GetString())
            .Should().Be((TerminalVersions.UpdateRequiredCode, "Updating, please wait."));

        terminal.Http.DefaultRequestHeaders.Remove(TerminalVersions.Header);
        terminal.Http.DefaultRequestHeaders.Add(TerminalVersions.Header, TerminalVersions.Minimum);
        var ok = await terminal.LoginWithPin(app.Pins["edna"]);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Headers.GetValues(TerminalVersions.ServerHeader).Single().Should().NotBeNullOrEmpty();
        app.Db(db => db.Terminals.Single(t => t.Id == terminal.TerminalId).AppVersion).Should().Be(TerminalVersions.Minimum);
    }

    [Fact]
    public async Task Settings_System_shows_the_version_and_says_why_this_server_cant_install_updates()
    {
        var admin = await app.AdminClient();
        var info = await (await admin.GetAsync("/api/admin/system")).Json();
        info.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        info.GetProperty("cantInstallReason").GetString().Should().NotBeNullOrEmpty("tests don't run as an installed service");
        var check = await (await admin.GetAsync("/api/admin/system/updates")).Json();
        check.GetProperty("available").GetBoolean().Should().BeFalse();
        check.GetProperty("error").GetString().Should().Contain("update server", "the feed isn't reachable from the tests");
        var apply = await admin.PostAsJsonAsync("/api/admin/system/updates/apply", new { force = false, confirm = (string?)null });
        apply.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_lost_admin_password_is_reset_with_the_token_printed_on_the_server()
    {
        var anon = app.CreateClient();
        (await anon.PostAsJsonAsync("/api/setup/recover", new { token = "AAAA-BBBB", userName = "admin", password = "a new password 1" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "no reset was asked for on the server");

        string token;
        await using (var scope = app.Services.CreateAsyncScope())
            token = await scope.ServiceProvider.GetRequiredService<AdminRecovery>().Issue(CancellationToken.None);

        var wrong = await anon.PostAsJsonAsync("/api/setup/recover", new { token = "AAAA-BBBB", userName = "admin", password = "a new password 1" });
        (await wrong.Json()).GetProperty("detail").GetString().Should().Contain("4 tries left");
        (await anon.PostAsJsonAsync("/api/setup/recover", new { token = token.ToLowerInvariant(), userName = "admin", password = "short" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var reset = await anon.PostAsJsonAsync("/api/setup/recover", new { token, userName = "admin", password = "a new password 1" });
        reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());

        (await app.AdminClient("admin", "a new password 1")).Should().NotBeNull();
        (await anon.PostAsJsonAsync("/api/setup/recover", new { token, userName = "admin", password = "another password 2" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the token works once");
        app.Db(db => db.AuditEntries.Any(a => a.Action == AuditActions.AdminPasswordReset && a.Actor == AdminRecovery.Actor)).Should().BeTrue();

        // Put the password back for the other tests in this collection.
        await using (var scope = app.Services.CreateAsyncScope())
            token = await scope.ServiceProvider.GetRequiredService<AdminRecovery>().Issue(CancellationToken.None);
        (await anon.PostAsJsonAsync("/api/setup/recover", new { token, userName = "admin", password = TausiApp.AdminPassword })).EnsureSuccessStatusCode();
    }
}
