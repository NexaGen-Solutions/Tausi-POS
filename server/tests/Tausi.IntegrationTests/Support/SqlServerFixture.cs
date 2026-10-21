using Microsoft.EntityFrameworkCore;
using Tausi.Infrastructure.Legacy;
using Tausi.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace Tausi.IntegrationTests.Support;

/// <summary>
/// One SQL Server for a test class: a container (needs Docker), or TAUSI_TEST_SQLSERVER. When
/// neither is there, <see cref="Available"/> is false and the tests skip.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string? ConnectionString { get; private set; }
    public bool Available => ConnectionString is not null;

    public async Task InitializeAsync()
    {
        var external = LegacyNames.Environment("TAUSI_TEST_SQLSERVER", "NEXAGEN_TEST_SQLSERVER");
        if (!string.IsNullOrEmpty(external))
        {
            ConnectionString = external;
            return;
        }
        if (!File.Exists("/var/run/docker.sock") && !OperatingSystem.IsWindows()) return;
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public string ConnectionFor(string database) =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = database }.ConnectionString;

    public DbContextOptions<SqlServerTausiDbContext> Options(string database) =>
        new DbContextOptionsBuilder<SqlServerTausiDbContext>().UseSqlServer(ConnectionFor(database), Slow).Options;

    /// <summary>SQL Server in a container can be slow while the rest of the suite runs alongside.</summary>
    public static void Slow(Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder o) => o.CommandTimeout(120);

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}
