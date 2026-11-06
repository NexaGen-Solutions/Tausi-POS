using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Tausi.Relay.Tests;

/// <summary>
/// A relay in memory (Sqlite), with a fake clock. Requests say where they come from with the
/// X-Test-Address header (Safaricom's 196.201.214.200 unless told otherwise).
/// </summary>
public sealed class RelayFixture : IAsyncDisposable
{
    public const string Safaricom = "196.201.214.200";
    public const string HashOfPassword = "admin-password-for-tests";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
    public WebApplication App { get; }
    public HttpClient Http { get; }

    public RelayFixture()
    {
        _connection.Open();
        App = RelayHost.Build(
        [
            "--Relay:PublicUrl=https://pay.example.test",
            "--Relay:SafaricomAddresses:0=196.201.214.200",
            "--Relay:SafaricomAddresses:1=196.201.212.0/24",
            "--Relay:AdminPasswordHash=" + AdminPasswords.Hash(HashOfPassword),
        ], b =>
        {
            b.WebHost.UseTestServer();
            b.Services.AddSingleton<TimeProvider>(Clock);
            b.Services.AddDbContext<RelayDb>(o => o.UseSqlite(_connection));
            b.Services.AddSingleton<IStartupFilter, TestAddress>();
        });
        RelayHost.Prepare(App).GetAwaiter().GetResult();
        App.StartAsync().GetAwaiter().GetResult();
        Http = App.GetTestClient();
    }

    public RelayDb Db() => App.Services.CreateScope().ServiceProvider.GetRequiredService<RelayDb>();

    /// <summary>A business, with its API key and callback path token.</summary>
    public async Task<(Business Business, string Key)> AddBusiness(string name)
    {
        var key = RelaySecrets.NewApiKey();
        var business = new Business
        {
            Name = name, ApiKeyHash = RelaySecrets.Hash(key), ApiKeyPrefix = RelaySecrets.PrefixOf(key), PathToken = RelaySecrets.NewPathToken(),
            CreatedAt = Clock.GetUtcNow().UtcDateTime,
        };
        await using var db = Db();
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        return (business, key);
    }

    public Task<HttpResponseMessage> Callback(string path, object body, string from = Safaricom)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: new System.Text.Json.JsonSerializerOptions()) };
        request.Headers.Add("X-Test-Address", from);
        return Http.SendAsync(request);
    }

    public HttpClient Server(string key)
    {
        var client = App.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    public static object C2B(string id, decimal amount, string? billRef = null) => new
    {
        TransactionType = "Buy Goods", TransID = id, TransTime = "20261106150512", TransAmount = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BusinessShortCode = "123456", BillRefNumber = billRef ?? "", MSISDN = "254712345678", FirstName = "JOHN", MiddleName = "", LastName = "K",
    };

    public static object Stk(string checkout, int resultCode, string? receipt = null, decimal amount = 1370) => new
    {
        Body = new
        {
            stkCallback = new
            {
                MerchantRequestID = "29115-34620561-1", CheckoutRequestID = checkout, ResultCode = resultCode,
                ResultDesc = resultCode == 0 ? "The service request is processed successfully." : "Request cancelled by user",
                CallbackMetadata = receipt is null ? null : new
                {
                    Item = new object[]
                    {
                        new { Name = "Amount", Value = amount }, new { Name = "MpesaReceiptNumber", Value = receipt },
                        new { Name = "TransactionDate", Value = 20261106214712 }, new { Name = "PhoneNumber", Value = 254712345678 },
                    },
                },
            },
        },
    };

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>Sets the caller's address from X-Test-Address, before anything else runs.</summary>
    private sealed class TestAddress : IStartupFilter
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
