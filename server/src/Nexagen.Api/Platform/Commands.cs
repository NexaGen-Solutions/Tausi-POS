using Nexagen.Infrastructure.Seeding;

namespace Nexagen.Api.Platform;

/// <summary>
/// Command-line tasks instead of starting the server:
/// <c>migrate</c> brings the database up to date;
/// <c>seed</c> adds roles, default permissions and ledger accounts;
/// <c>seed --demo</c> also adds the demo business, staff (PINs printed once), terminals and printers.
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
            case "seed":
                await Seed(app, demo: args.Contains("--demo"));
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
            .SeedDemo(Environment.GetEnvironmentVariable(Seeder.AdminPasswordVariable), CancellationToken.None);

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
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Seeder.AdminPasswordVariable)))
            Console.WriteLine($"Set {Seeder.AdminPasswordVariable} and run the seed again to give Administrator (user name: admin) an admin-site password.");
        else
            Console.WriteLine("Admin site: user name 'admin' with the password from " + Seeder.AdminPasswordVariable + ".");
    }
}
