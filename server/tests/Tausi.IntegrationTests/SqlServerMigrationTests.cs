using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tausi.Application;
using Tausi.Application.Abstractions;
using Tausi.Application.Auth;
using Tausi.Application.Common;
using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.Service;
using Tausi.Domain.Settings;
using Tausi.Infrastructure.Persistence;
using Tausi.Infrastructure.Seeding;
using Tausi.IntegrationTests.Support;

namespace Tausi.IntegrationTests;

/// <summary>SQL Server: the migrations, the ticket number sequence, row locks and concurrent printing. Needs Docker (or TAUSI_TEST_SQLSERVER).</summary>
public class SqlServerMigrationTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    private const string Skipped = "No Docker and no TAUSI_TEST_SQLSERVER: skipping the SQL Server check.";

    [SkippableFact]
    public async Task Migrations_apply_on_sql_server()
    {
        Skip.IfNot(sql.Available, Skipped);
        var options = sql.Options("tausi_migrations");
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

    [SkippableFact]
    public async Task Two_print_presses_at_once_print_one_round_on_sql_server()
    {
        Skip.IfNot(sql.Available, Skipped);
        var options = sql.Options("tausi_print");
        await using (var db = new SqlServerTausiDbContext(options))
        {
            await db.Database.MigrateAsync();
            await new Seeder(db, new PlainPinHasher(), NullLogger<Seeder>.Instance).SeedDemo("pw", CancellationToken.None, filePrinters: true);
            db.WorkPeriods.Add(new WorkPeriod { OpenedAt = DateTimeOffset.UtcNow, OpenedByUserId = db.Users.Single(u => u.Name == "MAU").Id });
            await db.SaveChangesAsync();
        }

        var session = new FixedSession();
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<SqlServerTausiDbContext>(o => o.UseSqlServer(sql.ConnectionFor("tausi_print"), SqlServerFixture.Slow));
        services.AddScoped<TausiDbContext>(sp => sp.GetRequiredService<SqlServerTausiDbContext>());
        services.AddScoped<ITausiDb>(sp => sp.GetRequiredService<TausiDbContext>());
        services.AddScoped<ITicketDatabase>(sp => new TicketDatabase(sp.GetRequiredService<TausiDbContext>()));
        services.AddSingleton<ICurrentSession>(session);
        services.AddSingleton<IApprovalTokenSource, NoApproval>();
        services.AddSingleton<IPosEvents, NoEvents>();
        services.AddSingleton<ILogoProvider, NoLogo>();
        services.AddSingleton<IPinHasher, PlainPinHasher>();
        await using var provider = services.BuildServiceProvider();

        TicketDetailDto ticket;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ITausiDb>();
            var edna = await db.Users.Include(u => u.Role).SingleAsync(u => u.Name == "edna");
            var counterA = await db.Terminals.SingleAsync(t => t.Name == "Counter A");
            var started = await scope.ServiceProvider.GetRequiredService<SessionKeeper>().Start(edna.Id, counterA.Id, CancellationToken.None);
            await db.SaveChangesAsync();
            session.User = new SessionUser(edna.Id, edna.Name, edna.RoleId, edna.Role!.Name, counterA.Id, counterA.Name, started.Id);
            session.Terminal = new DeviceTerminal(counterA.Id, counterA.Name);
            var punch = await db.MenuItems.SingleAsync(i => i.Name == "Pineapple Punch");
            ticket = await scope.ServiceProvider.GetRequiredService<StartTicket>().Execute(
                new StartTicketRequest(null, null, [new NewLineRequest(punch.Id, 2)]), CancellationToken.None);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var scope = provider.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<PrintTicket>().Print(ticket.Id, CancellationToken.None);
                return "printed";
            }
            catch (ConflictException e)
            {
                return e.Code;
            }
        }));

        results.Should().BeEquivalentTo(["printed", PrintCodes.NothingNew]);
        await using var check = new SqlServerTausiDbContext(options);
        (await check.PrintJobs.CountAsync(j => j.TicketId == ticket.Id)).Should().Be(2, "one bill and one dispense slip");
        (await check.Tickets.SingleAsync(k => k.Id == ticket.Id)).BillNumber.Should().Be(1);
    }
}
