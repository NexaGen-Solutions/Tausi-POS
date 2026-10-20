using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

[Collection(AppCollection.Name)]
public class IdempotencyTests(TausiApp app)
{
    private static HttpRequestMessage Post(string url, string key, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Fact]
    public async Task Repeated_key_returns_the_original_response_without_running_again()
    {
        var client = app.CreateClient();
        var key = Guid.NewGuid().ToString();
        var before = app.Db(db => db.TerminalPairings.Count());

        var first = await client.SendAsync(Post("/api/terminals/pairing", key));
        var second = await client.SendAsync(Post("/api/terminals/pairing", key));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.GetValues("Idempotency-Replayed").Should().Equal("true");
        (await second.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        app.Db(db => db.TerminalPairings.Count()).Should().Be(before + 1);
    }

    [Fact]
    public async Task Same_key_with_a_different_body_is_422()
    {
        var terminal = await app.PairTerminal();
        var key = Guid.NewGuid().ToString();

        (await terminal.Http.SendAsync(Post("/api/auth/pin", key, new { pin = app.Pins["CAROL"] }))).StatusCode.Should().Be(HttpStatusCode.OK);
        var other = await terminal.Http.SendAsync(Post("/api/auth/pin", key, new { pin = app.Pins["edna"] }));

        other.StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Failed_requests_are_not_stored_so_a_retry_runs_again()
    {
        var terminal = await app.PairTerminal();
        await terminal.LoginWithPin(app.Pins["edna"]);
        var key = Guid.NewGuid().ToString();

        (await terminal.Http.SendAsync(Post("/api/dev/void-probe", key))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var token = (await (await terminal.Http.PostAsJsonAsync("/api/auth/approve",
            new { pin = app.Pins["MAU"], permissionKey = "order.void" })).Json()).GetProperty("approvalToken").GetString();
        var retry = Post("/api/dev/void-probe", key);
        retry.Headers.Add("X-Approval-Token", token);
        (await terminal.Http.SendAsync(retry)).StatusCode.Should().Be(HttpStatusCode.OK);

        // And now the success is what a repeat gets back.
        var repeat = await terminal.Http.SendAsync(Post("/api/dev/void-probe", key));
        repeat.StatusCode.Should().Be(HttpStatusCode.OK);
        repeat.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
    }

    [Fact]
    public async Task Requests_without_a_key_are_not_affected()
    {
        var client = app.CreateClient();
        var a = await (await client.PostAsync("/api/terminals/pairing", null)).Json();
        var b = await (await client.PostAsync("/api/terminals/pairing", null)).Json();

        a.GetProperty("code").GetString().Should().NotBe(b.GetProperty("code").GetString());
    }
}
