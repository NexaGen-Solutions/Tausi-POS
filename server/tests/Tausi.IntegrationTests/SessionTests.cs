using System.Net;
using FluentAssertions;
using Tausi.Application.Auditing;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(ClockCollection.Name)]
public class SessionTests(ClockedApp app)
{
    private static async Task ShouldBeExpired(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Json()).GetProperty("code").GetString().Should().Be("session_expired");
    }

    [Fact]
    public async Task Ten_idle_minutes_end_the_session_and_any_request_before_that_refreshes_it()
    {
        var terminal = await app.PairTerminal();
        (await terminal.LoginWithPin(app.Pins["edna"])).EnsureSuccessStatusCode();

        app.Clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        (await terminal.Http.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);
        app.Clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        (await terminal.Http.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        app.Clock.Advance(TimeSpan.FromMinutes(10));
        await ShouldBeExpired(await terminal.Http.GetAsync("/api/auth/session"));

        // Ended for good, even right away.
        await ShouldBeExpired(await terminal.Http.GetAsync("/api/auth/me"));
    }

    [Fact]
    public async Task A_session_lasts_twelve_hours_at_most()
    {
        var terminal = await app.PairTerminal();
        (await terminal.LoginWithPin(app.Pins["CAROL"])).EnsureSuccessStatusCode();

        for (var i = 0; i < 89; i++)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(8));
            (await terminal.Http.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        app.Clock.Advance(TimeSpan.FromMinutes(8));
        (await terminal.Http.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Log_out_ends_the_session_and_is_audited()
    {
        var terminal = await app.PairTerminal();
        (await terminal.LoginWithPin(app.Pins["Ruth"])).EnsureSuccessStatusCode();

        (await terminal.Http.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await ShouldBeExpired(await terminal.Http.GetAsync("/api/auth/session"));
        app.Db(db => db.AuditEntries.Count(a => a.Action == AuditActions.Logout && a.TerminalId == terminal.TerminalId))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_new_login_on_the_terminal_ends_the_previous_one()
    {
        var terminal = await app.PairTerminal();
        (await terminal.LoginWithPin(app.Pins["edna"])).EnsureSuccessStatusCode();
        var first = terminal.Http.DefaultRequestHeaders.Authorization;

        (await terminal.LoginWithPin(app.Pins["WAMBOO"])).EnsureSuccessStatusCode();
        (await terminal.Http.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);

        terminal.Http.DefaultRequestHeaders.Authorization = first;
        await ShouldBeExpired(await terminal.Http.GetAsync("/api/auth/session"));
    }
}
