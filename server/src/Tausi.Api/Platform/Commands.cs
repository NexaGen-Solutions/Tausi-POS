using Microsoft.EntityFrameworkCore;
using Tausi.Application.Abstractions;
using Tausi.Infrastructure.Legacy;
using Tausi.Infrastructure.Seeding;

namespace Tausi.Api.Platform;

/// <summary>
/// Command-line tasks instead of starting the server:
/// <c>migrate</c> brings the database up to date;
/// <c>seed</c> adds roles, default permissions and ledger accounts;
/// <c>seed --demo</c> (Development only, as are the commands after it) also adds the demo business, staff (PINs printed once), terminals, printers and,
/// when they are empty, the demo menu, tables and a few open tickets; <c>seed --demo-menu</c> adds
/// only the demo menu; <c>require-pin-change &lt;name&gt;</c> makes that user choose a new PIN at their
/// next login; <c>close-day</c> closes the open day without a cash-up, for testing;
/// <c>check</c> checks the books, settled tickets and stock agree;
/// <c>reset --confirm</c> (Development only) empties the database so the next start is a new install.
/// </summary>
public static class Commands
{
    /// <summary>
    /// Why a command is refused here, or null. Seeding demo data, forcing a PIN change from the command
    /// line, closing the day without a cash-up and emptying the database are for development only: an
    /// installed server (Production) refuses them. <c>seed</c> alone (roles, permissions, accounts) stays.
    /// </summary>
    public static string? RefusedOutsideDevelopment(string[] args, bool development)
    {
        if (development || args.Length == 0) return null;
        var name = args[0] switch
        {
            "seed" when args.Contains("--demo") || args.Contains("--demo-menu") => $"seed {args.First(a => a.StartsWith("--demo", StringComparison.Ordinal))}",
            "require-pin-change" or "close-day" or "reset" => args[0],
            _ => null,
        };
        return name is null ? null : name switch
        {
            "close-day" => "close-day is for development only. Days are closed with the cash-up.",
            "require-pin-change" => "require-pin-change is for development only. Reset a PIN on the admin site (Staff and PINs).",
            _ => $"{name} is for development only. It isn't part of an installed server.",
        };
    }

    public static async Task<bool> TryRun(WebApplication app, string[] args)
    {
        if (RefusedOutsideDevelopment(args, app.Environment.IsDevelopment()) is { } refused)
        {
            Console.WriteLine(refused);
            Environment.ExitCode = 2;
            return true;
        }
        switch (args[0])
        {
            case "migrate":
                // Only in the installer's rollback test build: a migration that fails.
                if (app.Configuration.GetValue("Tausi:Test:FailMigration", false))
                {
                    Console.Error.WriteLine("Migration failed (Tausi:Test:FailMigration is set: a test build).");
                    Environment.ExitCode = 1;
                    return true;
                }
                await DatabaseStartup.Migrate(app.Services.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None);
                Console.WriteLine("Database is up to date.");
                return true;
            case "seed" when args.Contains("--demo-menu"):
                await DatabaseStartup.Migrate(app.Services.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None);
                await using (var scope = app.Services.CreateAsyncScope())
                    Console.WriteLine(await scope.ServiceProvider.GetRequiredService<Seeder>().SeedDemoMenu(CancellationToken.None)
                        ? "Demo menu added."
                        : "There is already a menu; nothing changed.");
                return true;
            case "seed":
                await Seed(app, demo: args.Contains("--demo"));
                return true;
            case "require-pin-change":
                await RequirePinChange(app, args.Skip(1).FirstOrDefault());
                return true;
            case "check":
                Environment.ExitCode = await Check(app) ? 0 : 1;
                return true;
            case "close-day":
                await CloseDay(app);
                return true;
            case "reset":
                await Reset(app, args.Contains("--confirm"));
                return true;
            default:
                return await InstallCommands.TryRun(app, args);
        }
    }

    /// <summary>
    /// Development only: deletes the database and makes it again, empty, so the next start is a new
    /// install (setup mode, a new setup token). Not part of the product.
    /// </summary>
    private static async Task Reset(WebApplication app, bool confirmed)
    {
        if (!confirmed)
        {
            Console.WriteLine("This deletes every ticket, user and setting in the database. Run `reset --confirm` to do it.");
            Environment.ExitCode = 1;
            return;
        }
        var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        await using (var scope = scopes.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.TausiDbContext>().Database.EnsureDeletedAsync();
        await DatabaseStartup.Migrate(scopes, CancellationToken.None);
        app.Services.GetRequiredService<Application.Setup.SetupGate>().Forget();
        await DatabaseStartup.ShowSetupToken(scopes, app.Logger, CancellationToken.None);
        Console.WriteLine("The database is empty again. Start the server and open /setup.");
    }

    private static async Task Seed(WebApplication app, bool demo)
    {
        var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        await DatabaseStartup.Migrate(scopes, CancellationToken.None);
        if (!demo)
        {
            Console.WriteLine("Roles, default permissions and ledger accounts are in place.");
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<Seeder>()
            .SeedDemo(Seeder.AdminPasswordFromEnvironment(), CancellationToken.None,
                filePrinters: app.Environment.IsDevelopment(), openDemoDay: true);

        if (created.Count == 0)
        {
            Console.WriteLine("Demo staff are already in place (existing PINs are not shown again). " +
                "The demo menu, tables and tickets were added where they were missing.");
            await DemoTicketsHint(scope);
            return;
        }
        Console.WriteLine();
        Console.WriteLine("New users. Write these PINs down now; they are not shown again.");
        Console.WriteLine();
        Console.WriteLine($"  {"Name",-15} {"Role",-12} PIN");
        foreach (var u in created)
            Console.WriteLine($"  {u.Name,-15} {u.Role,-12} {u.Pin}");
        await DemoTicketsHint(scope);
        Console.WriteLine();
        foreach (var warning in LegacyNames.TakeWarnings()) Console.WriteLine("Warning: " + warning);
        if (string.IsNullOrEmpty(Seeder.AdminPasswordFromEnvironment()))
            Console.WriteLine($"Set {Seeder.AdminPasswordVariable} and run the seed again to give Administrator (user name: admin) an admin-site password.");
        else
            Console.WriteLine("Admin site: user name 'admin' with the password from " + Seeder.AdminPasswordVariable + ".");
    }

    /// <summary>
    /// <c>check</c>: the books balance, settled tickets are paid with one sale entry each, stock matches its
    /// movements (after a power cut, or for support). Reads only; exit code 1 when something is off.
    /// </summary>
    private static async Task<bool> Check(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var report = await scope.ServiceProvider.GetRequiredService<Tausi.Application.Support.ConsistencyCheck>().Run(CancellationToken.None);
        Console.WriteLine($"Checked {report.SettledTickets} settled tickets and {report.JournalEntries} journal entries " +
                          $"(debit {report.TotalDebit:0.00}, credit {report.TotalCredit:0.00}).");
        foreach (var problem in report.Problems) Console.WriteLine($"  {problem.Area}: {problem.Detail}");
        Console.WriteLine(report.Ok ? "Everything agrees." : $"{report.Problems.Count} problem(s) found.");
        return report.Ok;
    }

    /// <summary>Demo tickets need an open day; say so when none is open.</summary>
    private static async Task DemoTicketsHint(AsyncServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<ITausiDb>();
        if (!await db.Tickets.AnyAsync() && !await db.WorkPeriods.AnyAsync(w => w.IsOpen))
            Console.WriteLine("No day is open, so no demo tickets were added. Open the day on a terminal, then run seed --demo again.");
    }

    /// <summary>Development only, until cash-up closes days: closes the open work period if no ticket is open.</summary>
    private static async Task CloseDay(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        // The same rule as closing on a terminal: refused while any ticket is open (part-paid ones too).
        try
        {
            var closed = await scope.ServiceProvider.GetRequiredService<Tausi.Application.Day.CloseWorkPeriod>()
                .Close(null, "close-day command", null, null, CancellationToken.None);
            Console.WriteLine(closed is not null ? "The day is closed." : "No day was open.");
        }
        catch (Tausi.Application.Day.OpenTicketsException e)
        {
            Console.WriteLine(e.Message);
            Environment.ExitCode = 1;
        }
    }

    private static async Task RequirePinChange(WebApplication app, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("Usage: require-pin-change <user name>, e.g. require-pin-change edna");
            Environment.ExitCode = 2;
            return;
        }
        await using var scope = app.Services.CreateAsyncScope();
        if (await scope.ServiceProvider.GetRequiredService<Seeder>().RequirePinChange(name, CancellationToken.None))
            Console.WriteLine($"{name} must choose a new PIN at their next login (they log in with their current PIN first).");
        else
        {
            Console.WriteLine($"No active user called '{name}'.");
            Environment.ExitCode = 1;
        }
    }
}
