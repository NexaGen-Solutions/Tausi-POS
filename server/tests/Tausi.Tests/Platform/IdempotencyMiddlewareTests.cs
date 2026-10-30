using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tausi.Api.Platform;
using Tausi.Application.Common;
using Tausi.Domain.Common;
using Tausi.Domain.Platform;
using Tausi.Infrastructure.Persistence;
using Tausi.Tests.Support;

namespace Tausi.Tests.Platform;

public class IdempotencyMiddlewareTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly FailingStore _store = new();
    private int _runs;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<TausiDbContext, SqliteTestDbContext>((sp, o) => o.UseSqlite(_connection).AddInterceptors(_store, sp.GetRequiredService<IdempotencyMark>()));
        builder.Services.AddScoped<IdempotencyMark>();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddProblemDetails();
        _app = builder.Build();
        _app.UseMiddleware<IdempotencyMiddleware>();
        _app.MapPost("/api/orders", (Order order) => new { run = ++_runs, order.Item });
        _app.MapPost("/api/other", () => new { run = ++_runs });
        _app.MapPost("/api/fails", () => { _runs++; return Results.BadRequest(); });
        _app.MapPost("/api/saves-then-breaks", async (TausiDbContext db) => { await Save(db); throw new TimeoutException("The database stalled."); });
        _app.MapPost("/api/breaks", (TausiDbContext _) => { _runs++; throw new TimeoutException("The database stalled."); });
        _app.MapPost("/api/rolls-back-then-breaks", async (TausiDbContext db) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Save(db);
            await transaction.RollbackAsync();
            throw new TimeoutException("The database stalled.");
        });
        _app.MapPost("/api/saves-then-refuses", async (TausiDbContext db) => { await Save(db); throw new ConflictException("Needs a supervisor."); });
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

    /// <summary>A request's own change: any row will do.</summary>
    private async Task Save(TausiDbContext db)
    {
        _runs++;
        db.IdempotencyRecords.Add(new IdempotencyRecord { Key = Guid.NewGuid().ToString(), CreatedAt = _clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> SendQuietly(string url, string key)
    {
        try
        {
            return await Send(url, key);
        }
        catch (Exception e) when (e is TimeoutException or ConflictException)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>Fails the save of a finished key (the answer) as often as asked, as a database stall would.</summary>
    private sealed class FailingStore : SaveChangesInterceptor
    {
        public int Failures;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var finishing = eventData.Context!.ChangeTracker.Entries<IdempotencyRecord>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Completed);
            if (finishing && Failures > 0)
            {
                Failures--;
                throw new DbUpdateException("Execution Timeout Expired.");
            }
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

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
    public async Task A_stall_storing_the_answer_is_tried_again()
    {
        _store.Failures = 2;
        var first = await Send("/api/orders", "k6", new Order("Tusker"));
        var second = await Send("/api/orders", "k6", new Order("Tusker"));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
        _runs.Should().Be(1);
    }

    [Fact]
    public async Task A_request_that_ran_never_runs_again_even_if_its_answer_cant_be_stored()
    {
        _store.Failures = 100;
        var first = await Send("/api/orders", "k7", new Order("Tusker"));
        var second = await Send("/api/orders", "k7", new Order("Tusker"));

        first.StatusCode.Should().Be(HttpStatusCode.OK, "the order went through, so the caller gets its answer");
        (await first.Content.ReadAsStringAsync()).Should().Contain("Tusker");
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _runs.Should().Be(1);
    }

    [Fact]
    public async Task A_fault_after_the_changes_committed_keeps_the_key_so_pressing_again_runs_nothing()
    {
        await SendQuietly("/api/saves-then-breaks", "k8");
        var again = await SendQuietly("/api/saves-then-breaks", "k8");

        _runs.Should().Be(1);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).Should().Contain(IdempotencyMiddleware.AlreadyDoneCode);
    }

    [Theory]
    [InlineData("/api/breaks")]
    [InlineData("/api/rolls-back-then-breaks")]
    public async Task A_fault_with_nothing_committed_frees_the_key_so_pressing_again_runs(string url)
    {
        await SendQuietly(url, "k9");
        await SendQuietly(url, "k9");

        _runs.Should().Be(2);
    }

    [Fact]
    public async Task A_refusal_frees_the_key_even_if_it_saved_something_so_an_approved_repeat_runs()
    {
        await SendQuietly("/api/saves-then-refuses", "k10");
        await SendQuietly("/api/saves-then-refuses", "k10");

        _runs.Should().Be(2);
    }

    [Fact]
    public async Task An_unfinished_key_that_committed_nothing_is_taken_over_only_once_it_is_stale()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TausiDbContext>();
            db.IdempotencyRecords.Add(new IdempotencyRecord { Key = "k11", Caller = "anonymous", Endpoint = "POST /api/orders",
                RequestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("""{"item":"Tusker"}"""))),
                CreatedAt = _clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        (await Send("/api/orders", "k11", new Order("Tusker"))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        _clock.Advance(IdempotencyMiddleware.StaleAfter + TimeSpan.FromSeconds(1));
        (await Send("/api/orders", "k11", new Order("Tusker"))).StatusCode.Should().Be(HttpStatusCode.OK);
        _runs.Should().Be(1);
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
