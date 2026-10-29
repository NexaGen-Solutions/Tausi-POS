using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Tausi.Api.Platform;
using Tausi.Domain.People;
using Tausi.Infrastructure.Legacy;
using Tausi.Infrastructure.Persistence;
using Tausi.Infrastructure.Seeding;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Tausi.IntegrationTests.Support;

/// <summary>
/// The real server on a real PostgreSQL. Uses TAUSI_TEST_POSTGRES (or the old NEXAGEN_TEST_POSTGRES) when set (CI provides a
/// service container), otherwise starts a throwaway container with Testcontainers.
/// Each run gets its own fresh database, seeded with the demo data.
/// </summary>
public class TausiApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminPassword = "integration-admin-pass";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string? ExternalServer = LegacyNames.Environment("TAUSI_TEST_POSTGRES", "NEXAGEN_TEST_POSTGRES");

    private PostgreSqlContainer? _container;
    private string _connectionString = "";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "tausi-it-" + Guid.NewGuid().ToString("N"));

    public string PrintOutput => Path.Combine(_dataDir, "print-output");

    /// <summary>The PINs the demo seed generated, by user name.</summary>
    public Dictionary<string, string> Pins { get; } = [];

    public async Task InitializeAsync()
    {
        _connectionString = await CreateDatabase();

        var scopes = Services.GetRequiredService<IServiceScopeFactory>();
        await DatabaseStartup.Migrate(scopes, CancellationToken.None);
        if (!SeedDemo) return;
        await using var scope = scopes.CreateAsyncScope();
        foreach (var user in await scope.ServiceProvider.GetRequiredService<Seeder>().SeedDemo(AdminPassword, CancellationToken.None))
            Pins[user.Name] = user.Pin;
        if (WaitersOnAnyTicket)
            Db(db => db.RolePermissions
                .Where(p => p.Role!.SystemKey == RoleKeys.Waiter && p.PermissionKey == PermissionKeys.TicketOthers)
                .ExecuteUpdate(u => u.SetProperty(p => p.Value, PermissionValue.Allowed)));
    }

    /// <summary>
    /// The tests written before ticket.others have waiters helping on each other's tickets; the
    /// tests of the rule itself (<see cref="OwnTicketsApp"/>) keep the default, Not allowed.
    /// </summary>
    protected virtual bool WaitersOnAnyTicket => true;

    /// <summary>A new database on the test PostgreSQL; returns its connection string.</summary>
    protected virtual async Task<string> CreateDatabase()
    {
        string server;
        if (!string.IsNullOrEmpty(ExternalServer))
        {
            server = ExternalServer;
        }
        else
        {
            _container = new PostgreSqlBuilder("postgres:16").Build();
            await _container.StartAsync();
            server = _container.GetConnectionString();
        }
        return new NpgsqlConnectionStringBuilder(server)
        {
            Database = "tausi_it_" + Guid.NewGuid().ToString("N")[..12],
        }.ConnectionString;
    }

    protected virtual string Provider => "Postgres";

    protected string ConnectionString => _connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Tausi", _connectionString);
        builder.UseSetting("Database:Provider", Provider);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Tausi:DataDirectory", _dataDir);
        builder.UseSetting("Tausi:Printing:Transport", PrintTransport);
        builder.UseSetting("Tausi:Printing:OutputDirectory", PrintOutput);
        builder.UseSetting("Web:Root", Path.Combine(_dataDir, "no-web"));
        builder.UseSetting("Tausi:BackupDirectory", Path.Combine(_dataDir, "backups"));
        builder.UseSetting("Tausi:Backups:Nightly", "false");
        builder.UseSetting("Tausi:Backups:PgDump", PgTools.Dump);
        builder.UseSetting("Tausi:Backups:PgRestore", PgTools.Restore);
        builder.UseSetting("Tausi:Updates:TerminalFolder", Path.Combine(_dataDir, "terminal-updates"));
        Settings(builder);
        // The tests print to File printers, which an installed server refuses (DevelopmentHost).
        builder.ConfigureTestServices(services => services.AddSingleton(new Tausi.Application.Common.DevelopmentHost(IsDevelopment: true)));
        builder.ConfigureTestServices(ConfigureTestServices);
    }

    /// <summary>False: a new install (migrated, base seed only), in setup mode.</summary>
    protected virtual bool SeedDemo => true;

    public string DataDirectory => _dataDir;

    /// <summary>"File" sends every printer's jobs to files; "Real" uses each printer's connection.</summary>
    protected virtual string PrintTransport => "File";

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    /// <summary>More settings for one kind of app.</summary>
    protected virtual void Settings(IWebHostBuilder builder)
    {
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await DropDatabase();
        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); }
            catch (UnauthorizedAccessException) { }
        }
    }

    protected virtual async Task DropDatabase()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }
        // A shared server: drop this run's database.
        NpgsqlConnection.ClearAllPools();
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        var database = builder.Database;
        builder.Database = "postgres";
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public T Db<T>(Func<TausiDbContext, T> read)
    {
        using var scope = Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<TausiDbContext>());
    }

    /// <summary>An HTTP client signed in to the admin site (as Administrator unless told otherwise).</summary>
    public async Task<HttpClient> AdminClient(string userName = "admin", string password = AdminPassword)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/api/admin/login", new { userName, password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Pairs a new terminal through the real pairing flow and returns a client carrying its device token.</summary>
    public async Task<TerminalClient> PairTerminal(string? name = null)
    {
        name ??= "Test " + Guid.NewGuid().ToString("N")[..8];
        var anon = CreateClient();
        var start = await (await anon.PostAsync("/api/terminals/pairing", null)).Json();
        var admin = await AdminClient();
        (await admin.PostAsJsonAsync("/api/admin/terminals/pair", new { code = start.GetProperty("code").GetString(), newTerminalName = name }))
            .EnsureSuccessStatusCode();
        var claim = await (await anon.PostAsJsonAsync("/api/terminals/pairing/claim", new
        {
            pairingId = start.GetProperty("pairingId").GetGuid(),
            secret = start.GetProperty("secret").GetString(),
        })).Json();

        var token = claim.GetProperty("deviceToken").GetString()!;
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Token", token);
        return new TerminalClient(client, token, claim.GetProperty("terminalId").GetGuid(), name);
    }
}

public sealed class TerminalClient(HttpClient http, string deviceToken, Guid terminalId, string name)
{
    public HttpClient Http { get; } = http;
    public string DeviceToken { get; } = deviceToken;
    public Guid TerminalId { get; } = terminalId;
    public string Name { get; } = name;
    public string? SessionToken { get; private set; }

    public async Task<HttpResponseMessage> LoginWithPin(string pin)
    {
        var response = await Http.PostAsJsonAsync("/api/auth/pin", new { pin });
        if (response.IsSuccessStatusCode)
        {
            SessionToken = (await response.Json()).GetProperty("token").GetString();
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);
        }
        return response;
    }
}

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<TausiApp>
{
    public const string Name = "app";
}

/// <summary>Its own server and database, on a clock the tests move by hand.</summary>
public sealed class ClockedApp : TausiApp
{
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<TimeProvider>(Clock);
}

[CollectionDefinition(Name)]
public sealed class ClockCollection : ICollectionFixture<ClockedApp>
{
    public const string Name = "clock";
}

/// <summary>Its own server and database, for tests that change staff.</summary>
public sealed class StaffApp : TausiApp;

[CollectionDefinition(Name)]
public sealed class StaffCollection : ICollectionFixture<StaffApp>
{
    public const string Name = "staff";
}

/// <summary>Its own server and database, for tests that change the menu.</summary>
public sealed class MenuApp : TausiApp;

[CollectionDefinition(Name)]
public sealed class MenuCollection : ICollectionFixture<MenuApp>
{
    public const string Name = "menu";
}

/// <summary>Its own server and database, for tests that change terminals and printers.</summary>
public sealed class DevicesApp : TausiApp
{
    protected override string PrintTransport => "Real";
}

[CollectionDefinition(Name)]
public sealed class DevicesCollection : ICollectionFixture<DevicesApp>
{
    public const string Name = "devices";
}

/// <summary>Its own server and database, so opening the day does not affect other tests.</summary>
public sealed class DayApp : TausiApp;

/// <summary>Its own server and database for tickets: it opens the day and changes permissions.</summary>
public sealed class TicketsApp : TausiApp;

/// <summary>Waiters with the default ticket.others (Not allowed): each works on their own tickets only.</summary>
public sealed class OwnTicketsApp : TausiApp
{
    protected override bool WaitersOnAnyTicket => false;
}

[CollectionDefinition(Name)]
public sealed class TicketsCollection : ICollectionFixture<TicketsApp>
{
    public const string Name = "tickets";
}

[CollectionDefinition(Name)]
public sealed class DayCollection : ICollectionFixture<DayApp>
{
    public const string Name = "day";
}

public static class HttpExtensions
{
    /// <summary>Reads the body as JSON. Safe to call more than once on the same response.</summary>
    public static async Task<JsonElement> Json(this HttpResponseMessage response)
    {
        await response.Content.LoadIntoBufferAsync();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), TausiApp.Json);
    }
}

/// <summary>A new install: migrated with the base seed only, so the server is in setup mode. Its clock moves by hand.</summary>
public sealed class FreshApp : TausiApp
{
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override bool SeedDemo => false;

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<TimeProvider>(Clock);
}

/// <summary>Its own server and database, for the SambaPOS import (it adds items, tables and staff).</summary>
public sealed class ImportApp : TausiApp;

[CollectionDefinition(Name)]
public sealed class OwnTicketsCollection : ICollectionFixture<OwnTicketsApp>
{
    public const string Name = "own-tickets";
}
