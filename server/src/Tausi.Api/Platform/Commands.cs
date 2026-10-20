using Tausi.Infrastructure.Legacy;
using Tausi.Infrastructure.Seeding;

namespace Tausi.Api.Platform;

/// <summary>
/// Command-line tasks instead of starting the server:
/// <c>migrate</c> brings the database up to date;
/// <c>seed</c> adds roles, default permissions and ledger accounts;
/// <c>seed --demo</c> also adds the demo business, staff (PINs printed once), terminals, printers and,
/// when the menu is empty, the demo menu; <c>seed --demo-menu</c> adds only the demo menu;
/// <c>require-pin-change &lt;name&gt;</c> makes that user choose a new PIN at their next login.
/// </summary>
public static class Commands
{
    public static async Task<bool> TryRun(WebApplication app, string[] args)
    {
        switch (args[0])
        {
            case "migrate":
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
            default:
                return false;
        }
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
                filePrinters: app.Environment.IsDevelopment());

        if (created.Count == 0)
        {
            Console.WriteLine("Demo data is already in place. Existing PINs are not shown again.");
            return;
        }
        Console.WriteLine();
        Console.WriteLine("New users. Write these PINs down now; they are not shown again.");
        Console.WriteLine();
        Console.WriteLine($"  {"Name",-15} {"Role",-12} PIN");
        foreach (var u in created)
            Console.WriteLine($"  {u.Name,-15} {u.Role,-12} {u.Pin}");
        Console.WriteLine();
        foreach (var warning in LegacyNames.TakeWarnings()) Console.WriteLine("Warning: " + warning);
        if (string.IsNullOrEmpty(Seeder.AdminPasswordFromEnvironment()))
            Console.WriteLine($"Set {Seeder.AdminPasswordVariable} and run the seed again to give Administrator (user name: admin) an admin-site password.");
        else
            Console.WriteLine("Admin site: user name 'admin' with the password from " + Seeder.AdminPasswordVariable + ".");
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
