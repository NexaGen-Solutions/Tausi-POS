using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(AppCollection.Name)]
public class ApprovalTests(TausiApp app)
{
    [Fact]
    public async Task A_waiter_cannot_approve()
    {
        var terminal = await app.PairTerminal();

        var response = await terminal.Http.PostAsJsonAsync("/api/auth/approve", new { pin = app.Pins["CYNTHIA"], permissionKey = "order.void" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Json()).GetProperty("approvalRequired").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Not_allowed_is_a_plain_403()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["WAMBOO"]);

        var response = await terminal.Http.GetAsync("/api/admin/settings");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await response.Json();
        problem.GetProperty("approvalRequired").GetBoolean().Should().BeFalse();
        problem.GetProperty("permission").GetString().Should().Be("settings.manage");
    }
}
