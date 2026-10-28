using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tausi.Api.Platform;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>A new install over HTTP: setup mode (423 and redirects), the setup token, the wizard and Finish.</summary>
public class SetupApiTests(FreshApp app) : IClassFixture<FreshApp>
{
    private HttpClient Browser() => app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    private static async Task Ok(HttpResponseMessage response) =>
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task A_new_install_is_set_up_through_the_wizard()
    {
        // Setup mode: the API answers 423, the pages go to /setup; the wizard, health and pairing still answer.
        var stranger = Browser();
        var locked = await stranger.GetAsync("/api/admin/reports");
        locked.StatusCode.Should().Be(HttpStatusCode.Locked);
        (await locked.Json()).GetProperty("code").GetString().Should().Be("setup_required");
        (await stranger.GetAsync("/admin/menu")).Headers.Location!.ToString().Should().Be("/setup");
        (await stranger.GetAsync("/pos/tables")).Headers.Location!.ToString().Should().Be("/setup");
        (await stranger.GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await stranger.PostAsync("/api/terminals/pairing", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await (await stranger.GetAsync("/api/setup/status")).Json();
        (status.GetProperty("complete").GetBoolean(), status.GetProperty("unlocked").GetBoolean()).Should().Be((false, false));

        // The token, printed at start and kept in setup-token.txt; five wrong tries lock it for five minutes.
        await DatabaseStartup.ShowSetupToken(app.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, CancellationToken.None);
        var token = (await File.ReadAllTextAsync(Path.Combine(app.DataDirectory, "setup-token.txt"))).Trim();
        var browser = Browser();
        for (var i = 0; i < 4; i++)
            (await browser.PostAsJsonAsync("/api/setup/unlock", new { token = "AAAA-AAAA" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await browser.PostAsJsonAsync("/api/setup/unlock", new { token = "AAAA-AAAA" })).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await browser.PostAsJsonAsync("/api/setup/unlock", new { token })).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await Ok(await browser.PostAsJsonAsync("/api/setup/unlock", new { token }));
        (await stranger.PutAsJsonAsync("/api/setup/business", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "only the unlocked browser");

        // Steps 1 and 2: the owner is signed in, so the later steps use the admin API.
        await Ok(await browser.PutAsJsonAsync("/api/setup/business", new
        {
            name = "Demo Bar", type = "Bar", address = "Ruiru", phone = "0700", vatRate = 16, currency = "KES", timeZone = "Africa/Nairobi",
        }));
        await Ok(await browser.PostAsJsonAsync("/api/setup/admin", new { fullName = "Wanjiku Njeri", userName = "wanjiku", password = "a long password", pin = "4826", pinAgain = "4826" }));
        var staff = await (await browser.PostAsJsonAsync("/api/setup/staff", new { people = new[] { new { name = "edna", role = "Waiter" }, new { name = "CAROL", role = "Cashier" } } })).Json();
        staff.GetProperty("people").GetArrayLength().Should().Be(2);
        staff.GetProperty("people")[0].GetProperty("pin").GetString().Should().MatchRegex("^[0-9]{4}$");
        (await browser.GetAsync("/api/admin/menu")).StatusCode.Should().Be(HttpStatusCode.OK, "the wizard's browser may use the admin API");
        (await stranger.GetAsync("/api/admin/menu")).StatusCode.Should().Be(HttpStatusCode.Locked);

        foreach (var step in new[] { "devices", "menu", "stock" }) await Ok(await browser.PostAsync($"/api/setup/steps/{step}/skip", null));
        (await browser.PostAsync("/api/setup/finish", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the licence step is required");
        await Ok(await browser.PostAsync("/api/setup/licence/trial", null));
        var summary = await (await browser.GetAsync("/api/setup/summary")).Json();
        summary.GetProperty("lines").EnumerateArray().Count(l => l.GetProperty("skipped").GetBoolean()).Should().Be(3);
        (await browser.PostAsync("/api/setup/finish", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Done: the token is gone, the API answers, and /setup says it's complete.
        File.Exists(Path.Combine(app.DataDirectory, "setup-token.txt")).Should().BeFalse();
        (await (await stranger.GetAsync("/api/setup/status")).Json()).GetProperty("complete").GetBoolean().Should().BeTrue();
        (await browser.GetAsync("/api/admin/menu")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await stranger.GetAsync("/api/admin/menu")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an ordinary sign-in now");
        (await browser.PostAsJsonAsync("/api/setup/unlock", new { token })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        app.Db(db => db.Users.Count()).Should().Be(3, "the owner and two staff: no bootstrap admin");
    }
}
