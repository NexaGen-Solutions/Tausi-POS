using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Tausi.Infrastructure.Legacy;
using Tausi.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace Tausi.IntegrationTests;

/// <summary>The SQL Server migrations apply cleanly and the ticket number sequence works. Needs Docker (or TAUSI_TEST_SQLSERVER).</summary>
public class SqlServerMigrationTests
{
    [SkippableFact]
    public async Task Migrations_apply_on_sql_server()
    {
        var external = LegacyNames.Environment("TAUSI_TEST_SQLSERVER", "NEXAGEN_TEST_SQLSERVER");
        Skip.If(string.IsNullOrEmpty(external) && !File.Exists("/var/run/docker.sock") && !OperatingSystem.IsWindows(),
            "No Docker and no TAUSI_TEST_SQLSERVER: skipping the SQL Server check.");

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
            var options = new DbContextOptionsBuilder<SqlServerTausiDbContext>().UseSqlServer(connection).Options;
            await using var db = new SqlServerTausiDbContext(options);
            await db.Database.MigrateAsync();

            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await db.Roles.CountAsync()).Should().Be(0);

            // Ticket numbers: parallel first-saves never get the same number.
            var numbers = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
            {
                await using var ctx = new SqlServerTausiDbContext(options);
                return await new TicketDatabase(ctx).NextNumber(CancellationToken.None);
            }));
            numbers.Should().OnlyHaveUniqueItems().And.OnlyContain(n => n >= TausiDbContext.FirstTicketNumber);

            // The row-lock statement runs on SQL Server (inside a transaction, as the use cases do).
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await new TicketDatabase(db).LockRows([Guid.NewGuid(), Guid.NewGuid()], CancellationToken.None);
                await tx.CommitAsync();
            }
        }
        finally
        {
            if (container is not null) await container.DisposeAsync();
        }
    }
}
