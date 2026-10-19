using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nexagen.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace Nexagen.IntegrationTests;

/// <summary>The SQL Server migrations apply cleanly. Needs Docker (or NEXAGEN_TEST_SQLSERVER).</summary>
public class SqlServerMigrationTests
{
    [SkippableFact]
    public async Task Migrations_apply_on_sql_server()
    {
        var external = Environment.GetEnvironmentVariable("NEXAGEN_TEST_SQLSERVER");
        Skip.If(string.IsNullOrEmpty(external) && !File.Exists("/var/run/docker.sock") && !OperatingSystem.IsWindows(),
            "No Docker and no NEXAGEN_TEST_SQLSERVER: skipping the SQL Server check.");

        MsSqlContainer? container = null;
        var connection = external;
        if (string.IsNullOrEmpty(connection))
        {
            container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await container.StartAsync();
            connection = container.GetConnectionString();
        }
        try
        {
            var options = new DbContextOptionsBuilder<SqlServerNexagenDbContext>().UseSqlServer(connection).Options;
            await using var db = new SqlServerNexagenDbContext(options);
            await db.Database.MigrateAsync();

            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await db.Roles.CountAsync()).Should().Be(0);
        }
        finally
        {
            if (container is not null) await container.DisposeAsync();
        }
    }
}
