using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Tausi.Application.Auditing;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(AppCollection.Name)]
public class ApprovalTests(TausiApp app)
{
    private static HttpRequestMessage Probe(string? approvalToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/dev/void-probe");
        if (approvalToken is not null) request.Headers.Add("X-Approval-Token", approvalToken);
        return request;
    }

    [Fact]
    public async Task Waiter_void_needs_approval_and_a_supervisor_token_works_exactly_once()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["edna"]);

        var first = await terminal.Http.SendAsync(Probe());
        first.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await first.Json();
        problem.GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
        problem.GetProperty("permission").GetString().Should().Be("order.void");

        var approval = await (await terminal.Http.PostAsJsonAsync("/api/auth/approve",
            new { pin = app.Pins["MAU"], permissionKey = "order.void" })).Json();
        var token = approval.GetProperty("approvalToken").GetString();
        approval.GetProperty("approvedByName").GetString().Should().Be("MAU");

        (await terminal.Http.SendAsync(Probe(token))).StatusCode.Should().Be(HttpStatusCode.OK);

        var reuse = await terminal.Http.SendAsync(Probe(token));
        reuse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reuse.Json()).GetProperty("approvalRequired").GetBoolean().Should().BeTrue();

        var edna = app.Db(db => db.Users.Single(u => u.Name == "edna").Id);
        var mau = app.Db(db => db.Users.Single(u => u.Name == "MAU").Id);
        var audit = app.Db(db => db.AuditEntries.Single(a => a.Action == AuditActions.DevVoidProbe && a.TerminalId == terminal.TerminalId));
        audit.UserId.Should().Be(edna);
        audit.ApprovedByUserId.Should().Be(mau);
    }

    [Fact]
    public async Task A_waiter_cannot_approve()
    {
        var terminal = await app.PairTerminal();

        var response = await terminal.Http.PostAsJsonAsync("/api/auth/approve", new { pin = app.Pins["CYNTHIA"], permissionKey = "order.void" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Json()).GetProperty("approvalRequired").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Supervisor_voids_without_approval()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["MAU"]);

        (await terminal.Http.SendAsync(Probe())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Not_allowed_is_a_plain_403()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["WAMBOO"]);

        var response = await terminal.Http.GetAsync("/api/settings/business");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await response.Json();
        problem.GetProperty("approvalRequired").GetBoolean().Should().BeFalse();
        problem.GetProperty("permission").GetString().Should().Be("settings.manage");
    }
}
