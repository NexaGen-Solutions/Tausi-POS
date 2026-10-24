using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tausi.Application;
using Tausi.Application.Abstractions;
using Tausi.Application.Auth;
using Tausi.Application.Events;
using Tausi.Application.Printing;
using Tausi.Application.Reports;
using Tausi.Application.Security;
using Tausi.Application.Tickets;
using Tausi.Domain.Audit;
using Tausi.Domain.Ledger;
using Tausi.Domain.Service;
using Tausi.Domain.Stock;
using Tausi.Infrastructure.Persistence;
using Tausi.Infrastructure.Seeding;
using Tausi.IntegrationTests.Support;
using Xunit.Abstractions;

namespace Tausi.IntegrationTests;

/// <summary>
/// A month of a busy bar (about 60,000 lines on 4,000 tickets with 6,000 payments, their stock
/// movements and journal entries): every report over the month comes back in under 2 seconds, on
/// PostgreSQL and on SQL Server. Each report runs once to warm up (query compilation), then is timed.
/// </summary>
public class ReportSpeedTests(PostgresFixture pg, SqlServerFixture sql, ITestOutputHelper output)
    : IClassFixture<PostgresFixture>, IClassFixture<SqlServerFixture>
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
    private static readonly DateOnly First = new(2026, 9, 1);
    private const int Days = 30;

    [Fact]
    public async Task Every_report_over_a_month_runs_in_under_2_seconds_on_postgresql()
    {
        var connection = pg.ConnectionFor("tausi_speed");
        await using (var db = new PostgresTausiDbContext(PostgresFixture.Options(connection)))
            await Seed(db);
        await AssertFast(services => services.AddDbContext<PostgresTausiDbContext>(o => o.UseNpgsql(connection))
            .AddScoped<TausiDbContext>(sp => sp.GetRequiredService<PostgresTausiDbContext>()), "PostgreSQL");
    }

    [SkippableFact]
    public async Task Every_report_over_a_month_runs_in_under_2_seconds_on_sql_server()
    {
        Skip.IfNot(sql.Available, "No Docker and no TAUSI_TEST_SQLSERVER: skipping the SQL Server check.");
        const string name = "tausi_speed";
        await using (var db = new SqlServerTausiDbContext(sql.Options(name)))
            await Seed(db);
        await AssertFast(services => services.AddDbContext<SqlServerTausiDbContext>(o => o.UseSqlServer(sql.ConnectionFor(name), SqlServerFixture.Slow))
            .AddScoped<TausiDbContext>(sp => sp.GetRequiredService<SqlServerTausiDbContext>()), "SQL Server");
    }

    private async Task AssertFast(Func<IServiceCollection, IServiceCollection> database, string databaseName)
    {
        var session = new FixedSession();
        var services = new ServiceCollection();
        services.AddApplication();
        database(services);
        services.AddScoped<ITausiDb>(sp => sp.GetRequiredService<TausiDbContext>());
        services.AddScoped<ITicketDatabase>(sp => new TicketDatabase(sp.GetRequiredService<TausiDbContext>()));
        services.AddScoped<Application.Stock.IStockDatabase>(sp => new StockDatabase(sp.GetRequiredService<TausiDbContext>()));
        services.AddScoped<Application.Ledger.ILedgerPostingService, Infrastructure.Ledger.LedgerPostingService>();
        services.AddSingleton<ICurrentSession>(session);
        services.AddSingleton<IApprovalTokenSource, NoApproval>();
        services.AddSingleton<IPosEvents, NoEvents>();
        services.AddSingleton<ILogoProvider, NoLogo>();
        services.AddSingleton<IPinHasher, PlainPinHasher>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ITausiDb>();
            var admin = await db.Users.Include(u => u.Role).SingleAsync(u => u.Name == "Administrator");
            var terminal = await db.Terminals.FirstAsync();
            session.User = new SessionUser(admin.Id, admin.Name, admin.RoleId, admin.Role!.Name, terminal.Id, terminal.Name, Guid.NewGuid());
        }

        var month = new ReportFilters { From = First, To = First.AddDays(Days - 1) };
        var sizes = new Dictionary<string, int>();
        foreach (var key in ReportService.Order)
        {
            await using (var warm = provider.CreateAsyncScope())
                await warm.ServiceProvider.GetRequiredService<ReportService>().Run(key, month, CancellationToken.None);
            await using var scope = provider.CreateAsyncScope();
            var watch = Stopwatch.StartNew();
            var result = await scope.ServiceProvider.GetRequiredService<ReportService>().Run(key, month, CancellationToken.None);
            watch.Stop();
            sizes[key] = result.Tables.Sum(t => t.Groups.Sum(g => g.Rows.Count));
            output.WriteLine($"{databaseName} {key}: {watch.ElapsedMilliseconds} ms, {sizes[key]} rows");
            watch.Elapsed.Should().BeLessThan(Limit, $"{key} on {databaseName}");
        }
        sizes["sales-by-user"].Should().BeGreaterThan(55_000, "the month really has about 60,000 lines");
        sizes["tickets"].Should().BeGreaterThan(3_900);
    }

    /// <summary>The demo seed, then a month of closed days built straight in the database.</summary>
    private static async Task Seed(TausiDbContext db)
    {
        db.Database.SetCommandTimeout(300);
        await db.Database.MigrateAsync();
        await new Seeder(db, new PlainPinHasher(), NullLogger<Seeder>.Instance).SeedDemo("pw", CancellationToken.None, filePrinters: true);
        await db.WorkPeriods.Where(p => p.IsOpen).ExecuteUpdateAsync(u => u.SetProperty(p => p.IsOpen, false).SetProperty(p => p.ClosedAt, DateTimeOffset.UtcNow));
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var waiters = await db.Users.Where(u => u.Role!.Name == "Waiter" || u.Role!.Name == "Supervisor").Select(u => u.Id).ToListAsync();
        var cashier = await db.Users.Where(u => u.Name == "CAROL").Select(u => u.Id).SingleAsync();
        var terminals = await db.Terminals.Select(t => t.Id).ToListAsync();
        var tables = await db.Tables.Select(t => t.Id).ToListAsync();
        var items = await db.MenuItems.Where(m => m.Active).Select(m => new { m.Id, m.Name }).ToListAsync();
        var stockItem = await db.StockItems.Select(i => i.Id).FirstAsync();
        var bar = await db.StockLocations.Where(l => l.Name == StockLocation.Bar).Select(l => l.Id).SingleAsync();
        var accounts = await db.LedgerAccounts.ToDictionaryAsync(a => a.Code, a => a.Id);
        var random = new Random(13);
        long number = 900_000, receipt = 700_000, code = 0;

        for (var d = 0; d < Days; d++)
        {
            var date = First.AddDays(d);
            var opened = new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
            var period = new WorkPeriod
            {
                OpenedAt = opened, ClosedAt = opened.AddHours(16), BusinessDate = date, IsOpen = false, OpenedByUserId = cashier,
            };
            db.WorkPeriods.Add(period);
            for (var t = 0; t < 134; t++)
            {
                var waiter = waiters[random.Next(waiters.Count)];
                var terminal = terminals[random.Next(terminals.Count)];
                var at = opened.AddMinutes(t * 7);
                var ticket = new Ticket
                {
                    Number = ++number, TableId = tables[random.Next(tables.Count)], Status = TicketStatus.Settled, OpenedByUserId = waiter,
                    OpenedAt = at, TerminalId = terminal, WorkPeriodId = period.Id, LastActivityAt = at.AddMinutes(40), SettledAt = at.AddMinutes(40),
                    SettledByUserId = cashier, SettledTerminalId = terminal, ReceiptNumber = ++receipt, BillNumber = 1, PrintRound = 1, BilledRound = 1,
                };
                for (var l = 0; l < 15; l++)
                {
                    var item = items[random.Next(items.Count)];
                    var line = new OrderLine
                    {
                        TicketId = ticket.Id, MenuItemId = item.Id, Name = item.Name, ShortName = item.Name, Status = OrderLineStatus.Printed,
                        PrintRound = 1, UnitPrice = 100 + random.Next(6) * 50, VatRate = 16, AddedByUserId = random.Next(4) == 0 ? waiters[random.Next(waiters.Count)] : waiter,
                        AddedTerminalId = terminal, AddedAt = at.AddMinutes(l), PrintedAt = at.AddMinutes(l), IsGift = random.Next(100) == 0,
                    };
                    line.SetQuantity(1 + random.Next(3));
                    if (random.Next(50) == 0)
                    {
                        line.Status = OrderLineStatus.Voided;
                        line.VoidedAt = at.AddMinutes(l + 1);
                        line.VoidedByUserId = cashier;
                        line.VoidReason = "Wrong item";
                        db.VoidLogs.Add(new VoidLog
                        {
                            TicketId = ticket.Id, OrderLineId = line.Id, MenuItemId = item.Id, ItemName = item.Name, Quantity = line.Quantity,
                            UnitPrice = line.UnitPrice, Amount = line.Gross, Reason = "Wrong item", RequestedByUserId = waiter, ApprovedByUserId = cashier,
                            TerminalId = terminal, At = line.VoidedAt.Value,
                        });
                    }
                    ticket.Lines.Add(line);
                    db.StockMovements.Add(new StockMovement
                    {
                        StockItemId = stockItem, LocationId = bar, Quantity = -line.Quantity, UnitCost = 40, Kind = StockMovementKind.Sale,
                        ReferenceType = Application.Stock.StockSelling.SaleReference, ReferenceId = line.Id, LineQuantity = line.Quantity,
                        UserId = waiter, At = line.AddedAt, WorkPeriodId = period.Id,
                    });
                }
                db.Tickets.Add(ticket);

                var total = ticket.Total;
                var split = random.Next(2) == 0;
                var cash = split ? Math.Round(total / 2) : total;
                var payments = new List<Payment>
                {
                    new() { TicketId = ticket.Id, Type = PaymentType.Cash, Amount = cash, TakenByUserId = cashier, BroughtByUserId = waiter, TerminalId = terminal,
                        WorkPeriodId = period.Id, TakenAt = at.AddMinutes(39), Status = PaymentStatus.Final },
                };
                if (split)
                    payments.Add(new Payment
                    {
                        TicketId = ticket.Id, Type = PaymentType.Mpesa, Amount = total - cash, MpesaCode = $"S{++code:000000000}", TakenByUserId = cashier,
                        BroughtByUserId = waiter, TerminalId = terminal, WorkPeriodId = period.Id, TakenAt = at.AddMinutes(39), Status = PaymentStatus.Final,
                    });
                db.Payments.AddRange(payments);

                var entry = new JournalEntry
                {
                    Date = ticket.SettledAt!.Value, Memo = $"Receipt {ticket.ReceiptNumber} · Ticket #{ticket.Number}", SourceType = "Sale",
                    SourceId = ticket.Id.ToString(), CreatedAt = ticket.SettledAt.Value, WorkPeriodId = period.Id, BusinessDate = date,
                };
                var net = ticket.Lines.Where(x => x.Counts).Sum(x => x.Net);
                var vat = ticket.Lines.Where(x => x.Counts).Sum(x => x.Vat);
                entry.Lines.Add(new JournalLine { AccountId = accounts[LedgerAccountCodes.Cash], Debit = cash });
                if (split) entry.Lines.Add(new JournalLine { AccountId = accounts[LedgerAccountCodes.MPesa], Debit = total - cash });
                entry.Lines.Add(new JournalLine { AccountId = accounts[LedgerAccountCodes.Sales], Credit = net });
                entry.Lines.Add(new JournalLine { AccountId = accounts[LedgerAccountCodes.VatPayable], Credit = vat });
                db.JournalEntries.Add(entry);
                db.AuditEntries.Add(new AuditEntry
                {
                    At = ticket.SettledAt.Value, UserId = cashier, TerminalId = terminal, Action = "ticket.settled", EntityType = nameof(Ticket),
                    EntityId = ticket.Id.ToString(), NewValueJson = $"{{\"ticket\":{ticket.Number},\"total\":{total}}}",
                });
            }
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
    }
}
