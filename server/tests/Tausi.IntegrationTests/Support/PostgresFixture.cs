using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Tausi.Infrastructure.Legacy;
using Tausi.Infrastructure.Persistence;

namespace Tausi.IntegrationTests.Support;

/// <summary>
/// A PostgreSQL server for tests that need databases of their own (not the shared app's):
/// TAUSI_TEST_POSTGRES when set (CI), else a throwaway container. Databases made here are dropped at the end.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _server = "";
    private readonly List<string> _databases = [];

    public async Task InitializeAsync()
    {
        var external = LegacyNames.Environment("TAUSI_TEST_POSTGRES", "NEXAGEN_TEST_POSTGRES");
        if (!string.IsNullOrEmpty(external))
        {
            _server = external;
            return;
        }
        _container = new PostgreSqlBuilder("postgres:16").Build();
        await _container.StartAsync();
        _server = _container.GetConnectionString();
    }

    public string ConnectionFor(string database)
    {
        var name = $"{database}_{Guid.NewGuid().ToString("N")[..8]}";
        _databases.Add(name);
        return new NpgsqlConnectionStringBuilder(_server) { Database = name, CommandTimeout = 120 }.ConnectionString;
    }

    public static DbContextOptions<PostgresTausiDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<PostgresTausiDbContext>().UseNpgsql(connection).Options;

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }
        NpgsqlConnection.ClearAllPools();
        var builder = new NpgsqlConnectionStringBuilder(_server) { Database = "postgres" };
        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        foreach (var database in _databases)
        {
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
