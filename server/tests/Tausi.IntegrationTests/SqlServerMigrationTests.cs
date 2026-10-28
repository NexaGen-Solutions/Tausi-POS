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
using Tausi.Domain.Common;
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
            db.WorkPeriods.Add(new WorkPeriod { OpenedAt = DateTimeOffset.UtcNow, OpenedByUserId = db.Users.Single(u => u.Name == "SAM").Id });
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
        services.AddScoped<Tausi.Application.Stock.IStockDatabase>(sp => new StockDatabase(sp.GetRequiredService<TausiDbContext>()));
        services.AddScoped<Application.Ledger.ILedgerPostingService, Infrastructure.Ledger.LedgerPostingService>();
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

    [SkippableFact]
    public async Task Two_supervisors_approving_one_request_at_once_make_one_void_on_sql_server()
    {
        Skip.IfNot(sql.Available, Skipped);
        const string name = "tausi_approve";
        var options = sql.Options(name);
        await Seeded(name);

        var (edna, _) = await As(name, "edna", "Counter A");
        var (sam, _) = await As(name, "SAM", "Counter B");
        var (admin, _) = await As(name, "Administrator", "Pillar 1");
        Guid requestId, ticketId;
        await using (var scope = edna.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ITausiDb>();
            var punch = await db.MenuItems.SingleAsync(i => i.Name == "Pineapple Punch");
            var ticket = await scope.ServiceProvider.GetRequiredService<StartTicket>().Execute(
                new StartTicketRequest(null, null, [new NewLineRequest(punch.Id, 2)]), CancellationToken.None);
            ticketId = ticket.Id;
            var printed = await scope.ServiceProvider.GetRequiredService<PrintTicket>().Print(ticket.Id, CancellationToken.None);
            var request = await scope.ServiceProvider.GetRequiredService<Application.Approvals.CreateApprovalRequest>().Execute(
                new Application.Approvals.CreateApprovalRequestDto(ApprovalKind.Void, ticket.Id, printed.Lines[0].Id,
                    System.Text.Json.JsonSerializer.SerializeToElement(new { quantity = 1, reason = "Wrong item" })), CancellationToken.None);
            requestId = request.Id;
        }

        var results = await Task.WhenAll(new[] { sam, admin }.Select(async provider =>
        {
            await using var scope = provider.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<Application.Approvals.ApproveRequest>().Execute(requestId, CancellationToken.None);
                return "approved";
            }
            catch (ConflictException e)
            {
                return e.Code;
            }
        }));

        results.Should().BeEquivalentTo(["approved", Application.Approvals.ApprovalCodes.AlreadyDecided]);
        await using var check = new SqlServerTausiDbContext(options);
        (await check.VoidLogs.CountAsync(v => v.TicketId == ticketId)).Should().Be(1);
        (await check.OrderLines.CountAsync(l => l.TicketId == ticketId && l.Status == OrderLineStatus.Voided)).Should().Be(1);
        (await check.ApprovalRequests.SingleAsync(r => r.Id == requestId)).Status.Should().Be(ApprovalStatus.Approved);
        await edna.DisposeAsync();
        await sam.DisposeAsync();
        await admin.DisposeAsync();
    }

    [SkippableFact]
    public async Task Concurrent_prints_of_two_tickets_with_the_same_item_keep_on_hand_consistent_on_sql_server()
    {
        Skip.IfNot(sql.Available, Skipped);
        const string name = "tausi_stock";
        var options = sql.Options(name);
        await Seeded(name);

        var waiters = new[] { await As(name, "edna", "Counter A"), await As(name, "Ruth", "Pillar 1") };
        var tickets = new List<(ServiceProvider Provider, Guid Id)>();
        foreach (var (provider, _) in waiters)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ITausiDb>();
            var tusker = await db.MenuItems.SingleAsync(i => i.Name == "Tusker Lager");
            var cold = await db.ModifierOptions.SingleAsync(o => o.Name == "Cold");
            var ticket = await scope.ServiceProvider.GetRequiredService<StartTicket>().Execute(
                new StartTicketRequest(null, null, [new NewLineRequest(tusker.Id, 3, [cold.Id])]), CancellationToken.None);
            tickets.Add((provider, ticket.Id));
        }
        decimal OnHand(SqlServerTausiDbContext db) =>
            db.StockLevels.Single(l => l.StockItemId == db.StockItems.Single(i => i.Name == "Tusker Lager").Id
                && l.LocationId == db.StockLocations.Single(x => x.Name == "Bar").Id).OnHand;
        decimal before;
        await using (var db = new SqlServerTausiDbContext(options)) before = OnHand(db);

        await Task.WhenAll(tickets.Select(async t =>
        {
            await using var scope = t.Provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PrintTicket>().Print(t.Id, CancellationToken.None);
        }));

        await using var check = new SqlServerTausiDbContext(options);
        OnHand(check).Should().Be(before - 6);
        var item = check.StockItems.Single(i => i.Name == "Tusker Lager").Id;
        var bar = check.StockLocations.Single(x => x.Name == "Bar").Id;
        check.StockMovements.Where(m => m.StockItemId == item && m.LocationId == bar).Sum(m => m.Quantity).Should().Be(OnHand(check));
        foreach (var (provider, _) in waiters) await provider.DisposeAsync();
    }

    /// <summary>A database with the demo seed and an open day.</summary>
    private async Task Seeded(string name)
    {
        await using var db = new SqlServerTausiDbContext(sql.Options(name));
        await db.Database.MigrateAsync();
        await new Seeder(db, new PlainPinHasher(), NullLogger<Seeder>.Instance).SeedDemo("pw", CancellationToken.None, filePrinters: true);
        db.WorkPeriods.Add(new WorkPeriod { OpenedAt = DateTimeOffset.UtcNow, OpenedByUserId = db.Users.Single(u => u.Name == "SAM").Id });
        await db.SaveChangesAsync();
    }

    /// <summary>Someone logged in on a terminal: their own service provider (their own session), on one database.</summary>
    private async Task<(ServiceProvider Provider, FixedSession Session)> As(string name, string user, string terminal)
    {
        var session = new FixedSession();
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<SqlServerTausiDbContext>(o => o.UseSqlServer(sql.ConnectionFor(name), SqlServerFixture.Slow));
        services.AddScoped<TausiDbContext>(sp => sp.GetRequiredService<SqlServerTausiDbContext>());
        services.AddScoped<ITausiDb>(sp => sp.GetRequiredService<TausiDbContext>());
        services.AddScoped<ITicketDatabase>(sp => new TicketDatabase(sp.GetRequiredService<TausiDbContext>()));
        services.AddScoped<Tausi.Application.Stock.IStockDatabase>(sp => new StockDatabase(sp.GetRequiredService<TausiDbContext>()));
        services.AddScoped<Application.Ledger.ILedgerPostingService, Infrastructure.Ledger.LedgerPostingService>();
        services.AddSingleton<ICurrentSession>(session);
        services.AddSingleton<IApprovalTokenSource, NoApproval>();
        services.AddSingleton<IPosEvents, NoEvents>();
        services.AddSingleton<ILogoProvider, NoLogo>();
        services.AddSingleton<IPinHasher, PlainPinHasher>();
        var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ITausiDb>();
        var u = await db.Users.Include(x => x.Role).SingleAsync(x => x.Name == user);
        var t = await db.Terminals.SingleAsync(x => x.Name == terminal);
        var started = await scope.ServiceProvider.GetRequiredService<SessionKeeper>().Start(u.Id, t.Id, CancellationToken.None);
        await db.SaveChangesAsync();
        session.User = new SessionUser(u.Id, u.Name, u.RoleId, u.Role!.Name, t.Id, t.Name, started.Id);
        session.Terminal = new DeviceTerminal(t.Id, t.Name);
        return (provider, session);
    }

    [SkippableFact]
    public async Task Receipt_numbers_never_repeat_and_one_ticket_settles_once_on_sql_server()
    {
        Skip.IfNot(sql.Available, Skipped);
        const string name = "tausi_settle";
        await Seeded(name);
        var (edna, _) = await As(name, "edna", "Counter A");
        var (carol, carolSession) = await As(name, "CAROL", "Counter B");

        var ids = new List<Guid>();
        await using (var scope = edna.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ITausiDb>();
            var punch = await db.MenuItems.SingleAsync(i => i.Name == "Pineapple Punch");
            for (var i = 0; i < 4; i++)
            {
                var ticket = await scope.ServiceProvider.GetRequiredService<StartTicket>().Execute(
                    new StartTicketRequest(null, $"T{i}", [new NewLineRequest(punch.Id, 1)]), CancellationToken.None);
                await scope.ServiceProvider.GetRequiredService<PrintTicket>().Print(ticket.Id, CancellationToken.None);
                ids.Add(ticket.Id);
            }
        }
        // CAROL opens them all at Counter B and takes a card payment on each.
        await using (var db = new SqlServerTausiDbContext(sql.Options(name)))
            await db.Tickets.Where(k => ids.Contains(k.Id)).ExecuteUpdateAsync(u => u
                .SetProperty(k => k.LockTerminalId, carolSession.User!.TerminalId)
                .SetProperty(k => k.LockUserId, carolSession.User!.UserId)
                .SetProperty(k => k.LockSessionId, carolSession.User!.SessionId)
                .SetProperty(k => k.LockExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5)));
        await using (var scope = carol.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<Application.Cash.OpenShift>().Execute(new Application.Cash.OpenShiftRequest(), CancellationToken.None);
        foreach (var id in ids)
        {
            await using var scope = carol.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Application.Settling.PaymentCommands>().Add(
                id, new Application.Settling.AddPaymentRequest(PaymentType.Card, 270m), CancellationToken.None);
        }

        // All four at once, plus the first one a second time.
        var results = await Task.WhenAll(ids.Append(ids[0]).Select(async id =>
        {
            await using var scope = carol.CreateAsyncScope();
            try
            {
                return (long?)(await scope.ServiceProvider.GetRequiredService<Application.Settling.SettleTicket>().Execute(id, CancellationToken.None)).ReceiptNumber;
            }
            catch (DomainException)
            {
                return null;
            }
        }));

        results.OfType<long>().Should().HaveCount(4).And.OnlyHaveUniqueItems();
        await using var check = new SqlServerTausiDbContext(sql.Options(name));
        (await check.JournalEntries.CountAsync(e => ids.Select(i => i.ToString()).Contains(e.SourceId!))).Should().Be(4);
        await edna.DisposeAsync();
        await carol.DisposeAsync();
    }
    [SkippableFact]
    public async Task The_back_fill_dates_existing_entries_on_sql_server()
    {
        Skip.IfNot(sql.Available, Skipped);
        const string name = "tausi_backfill";
        await Seeded(name);
        var id = Guid.NewGuid();
        await using (var db = new SqlServerTausiDbContext(sql.Options(name)))
        {
            db.JournalEntries.Add(new Domain.Ledger.JournalEntry { Id = id, Date = DateTimeOffset.UtcNow.AddDays(-3), Memo = "Before task 12", SourceType = "Test", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await new Application.Money.LedgerBackfill(db).Run(CancellationToken.None);
        }

        await using var check = new SqlServerTausiDbContext(sql.Options(name));
        (await check.JournalEntries.SingleAsync(e => e.Id == id)).BusinessDate.Should().NotBeNull();
        (await check.JournalEntries.CountAsync(e => e.BusinessDate == null)).Should().Be(0);
    }
}
