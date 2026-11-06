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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tausi.Application.Mpesa;
using Tausi.Relay;

namespace Tausi.IntegrationTests.Support;

/// <summary>
/// A bar server with M-Pesa: the real Tausi relay running in memory (its own Sqlite database) and a
/// fake Daraja. Safaricom's callbacks are posted to the relay from its address (196.201.214.200).
/// </summary>
public sealed class MpesaApp : TausiApp
{
    private readonly SqliteConnection _relayDb = new("Data Source=:memory:");
    private WebApplication? _relay;

    public FakeDaraja Daraja { get; } = new();

    public WebApplication Relay
    {
        get
        {
            if (_relay is not null) return _relay;
            _relayDb.Open();
            _relay = RelayHost.Build(["--Relay:PublicUrl=https://pay.example.test", "--Relay:SafaricomAddresses:0=196.201.214.200"], b =>
            {
                b.WebHost.UseTestServer();
                b.Services.AddDbContext<RelayDb>(o => o.UseSqlite(_relayDb));
                b.Services.AddSingleton<IStartupFilter, CallerAddress>();
            });
            RelayHost.Prepare(_relay).GetAwaiter().GetResult();
            _relay.StartAsync().GetAwaiter().GetResult();
            return _relay;
        }
    }

    /// <summary>A business on the relay: its API key and path token.</summary>
    public async Task<(string Key, string Token)> RelayBusiness()
    {
        var key = RelaySecrets.NewApiKey();
        var token = RelaySecrets.NewPathToken();
        await using var scope = Relay.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDb>();
        db.Businesses.Add(new Business
        {
            Name = "Demo Bar", ApiKeyHash = RelaySecrets.Hash(key), ApiKeyPrefix = RelaySecrets.PrefixOf(key), PathToken = token, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (key, token);
    }

    /// <summary>Safaricom posting to the relay.</summary>
    public async Task Safaricom(string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: new System.Text.Json.JsonSerializerOptions()) };
        request.Headers.Add("X-Test-Address", "196.201.214.200");
        (await Relay.GetTestClient().SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IDaraja>();
        services.AddSingleton<IDaraja>(Daraja);
        services.RemoveAll<IRelayClient>();
        services.AddSingleton<IRelayClient>(_ => new Tausi.Infrastructure.Mpesa.HttpRelayClient(Relay.GetTestClient()));
    }

    private sealed class CallerAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, go) =>
            {
                http.Connection.RemoteIpAddress = IPAddress.Parse(http.Request.Headers["X-Test-Address"].FirstOrDefault() ?? "10.9.9.9");
                return go(http);
            });
            next(app);
        };
    }
}

/// <summary>Daraja for the API tests: every call succeeds; prompts get "ws_CO_1", "ws_CO_2"….</summary>
public sealed class FakeDaraja : IDaraja
{
    public List<(string ShortCode, string Confirmation)> Registrations { get; } = [];
    private int _prompts;

    public Task CheckCredentials(MpesaCredentials credentials, bool live, CancellationToken ct) => Task.CompletedTask;

    public Task<StkPushResult> StkPush(MpesaCredentials credentials, bool live, StkPushRequest request, CancellationToken ct) =>
        Task.FromResult(new StkPushResult($"ws_CO_{Interlocked.Increment(ref _prompts)}", null, null));

    public Task<StkQueryResult> StkQuery(MpesaCredentials credentials, bool live, string shortCode, string checkoutRequestId, CancellationToken ct) =>
        Task.FromResult(new StkQueryResult(null, null));

    public Task<byte[]> DynamicQr(MpesaCredentials credentials, bool live, DynamicQrRequest request, CancellationToken ct) =>
        Task.FromException<byte[]>(new DarajaException("No QR in these tests."));

    public Task RegisterC2B(MpesaCredentials credentials, bool live, string shortCode, string confirmationUrl, string validationUrl, CancellationToken ct)
    {
        Registrations.Add((shortCode, confirmationUrl));
        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public sealed class MpesaCollection : ICollectionFixture<MpesaApp>
{
    public const string Name = "mpesa";
}
