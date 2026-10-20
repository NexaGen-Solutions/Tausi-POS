using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tausi.Application.Security;
using Tausi.Domain.People;
using Tausi.Infrastructure.Persistence;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(AppCollection.Name)]
public class PinLoginScreenTests(TausiApp app)
{
    /// <summary>Adds a waiter directly, for tests that change them.</summary>
    private async Task<string> AddWaiter(string pin, bool active = true, bool mustChangePin = false)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TausiDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPinHasher>();
        var name = "Waiter " + Guid.NewGuid().ToString("N")[..6];
        db.Users.Add(new User
        {
            Name = name,
            RoleId = db.Roles.Single(r => r.Name == "Waiter").Id,
            PinHash = hasher.Hash(pin),
            PinLookup = hasher.Lookup(pin),
            PinLength = pin.Length,
            Active = active,
            MustChangePin = mustChangePin,
        });
        await db.SaveChangesAsync();
        return name;
    }

    [Fact]
    public async Task An_unpaired_device_cannot_log_in()
    {
        var response = await app.CreateClient().PostAsJsonAsync("/api/auth/pin", new { pin = app.Pins["edna"] });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_inactive_user_gets_the_same_answer_as_a_wrong_pin()
    {
        await AddWaiter("7391", active: false);
        var terminal = await app.PairTerminal();

        var inactive = await terminal.LoginWithPin("7391");
        var wrong = await terminal.LoginWithPin("0000");

        inactive.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await inactive.Json()).GetProperty("detail").GetString()
            .Should().Be((await wrong.Json()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Max_pin_length_is_only_a_number()
    {
        var terminal = await app.PairTerminal();

        var response = await terminal.Http.GetAsync("/api/auth/pin/max-length");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        int.TryParse(body, out var length).Should().BeTrue($"the body should be a bare number, not {body}");
        length.Should().BeInRange(4, 6);
        (await app.CreateClient().GetAsync("/api/auth/pin/max-length")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_says_how_long_the_terminal_may_stay_idle()
    {
        var terminal = await app.PairTerminal();
        var login = await (await terminal.LoginWithPin(app.Pins["edna"])).Json();

        login.GetProperty("idleLogoutMinutes").GetInt32().Should().Be(10);
        login.GetProperty("mustChangePin").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_user_who_must_change_their_pin_can_only_do_that()
    {
        await AddWaiter("6284", mustChangePin: true);
        var terminal = await app.PairTerminal();
        var login = await (await terminal.LoginWithPin("6284")).Json();
        login.GetProperty("mustChangePin").GetBoolean().Should().BeTrue();

        var blocked = await terminal.Http.PostAsync("/api/dev/void-probe", null);
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Json()).GetProperty("code").GetString().Should().Be("pin_change_required");

        async Task<string?> Change(string pin)
        {
            var response = await terminal.Http.PostAsJsonAsync("/api/auth/pin/change", new { newPin = pin });
            return response.StatusCode == HttpStatusCode.NoContent ? null : (await response.Json()).GetProperty("code").GetString();
        }
        (await Change(app.Pins["CAROL"])).Should().Be("pin_taken");
        (await Change("1234")).Should().Be("pin_too_simple");
        (await Change("6284")).Should().Be("pin_unchanged");
        (await Change("50719")).Should().BeNull();

        (await terminal.Http.PostAsync("/api/dev/void-probe", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "now it is the normal approval rule, not the PIN gate");
        (await new TerminalClient(terminal.Http, terminal.DeviceToken, terminal.TerminalId, terminal.Name).LoginWithPin("50719"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
