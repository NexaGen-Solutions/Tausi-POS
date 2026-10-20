using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tausi.Api.Platform;
using Tausi.Infrastructure.Persistence;
using Tausi.Tests.Support;

namespace Tausi.Tests.Platform;

public class IdempotencyMiddlewareTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _runs;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<TausiDbContext, SqliteTestDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddProblemDetails();
        _app = builder.Build();
        _app.UseMiddleware<IdempotencyMiddleware>();
        _app.MapPost("/api/orders", (Order order) => new { run = ++_runs, order.Item });
        _app.MapPost("/api/other", () => new { run = ++_runs });
        _app.MapPost("/api/fails", () => { _runs++; return Results.BadRequest(); });
        await _app.StartAsync();
        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<TausiDbContext>().Database.EnsureCreatedAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed record Order(string Item);

    private Task<HttpResponseMessage> Send(string url, string? key, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Repeat_returns_the_stored_response_and_does_not_run_again()
    {
        var first = await Send("/api/orders", "k1", new Order("Tusker"));
        var second = await Send("/api/orders", "k1", new Order("Tusker"));

        _runs.Should().Be(1);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        second.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
    }

    [Fact]
    public async Task Same_key_with_another_body_is_422()
    {
        await Send("/api/orders", "k2", new Order("Tusker"));
        var other = await Send("/api/orders", "k2", new Order("Guinness"));

        other.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _runs.Should().Be(1);
    }

    [Fact]
    public async Task Same_key_on_another_endpoint_is_422()
    {
        await Send("/api/orders", "k3", new Order("Tusker"));

        (await Send("/api/other", "k3", new Order("Tusker"))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Failures_are_not_stored_so_a_retry_runs()
    {
        await Send("/api/fails", "k4");
        await Send("/api/fails", "k4");

        _runs.Should().Be(2);
    }

    [Fact]
    public async Task No_key_means_no_idempotency()
    {
        await Send("/api/orders", null, new Order("Tusker"));
        await Send("/api/orders", null, new Order("Tusker"));

        _runs.Should().Be(2);
    }

    [Fact]
    public async Task Keys_last_24_hours()
    {
        await Send("/api/orders", "k5", new Order("Tusker"));
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            _clock.Advance(TimeSpan.FromHours(25));
            await Tausi.Infrastructure.Platform.HousekeepingService.CleanUp(
                scope.ServiceProvider.GetRequiredService<TausiDbContext>(), _clock.GetUtcNow(), CancellationToken.None);
        }

        await Send("/api/orders", "k5", new Order("Tusker"));
        _runs.Should().Be(2);
    }
}
