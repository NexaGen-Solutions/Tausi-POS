using Microsoft.EntityFrameworkCore;
using Tausi.Infrastructure.Persistence;
using Tausi.Infrastructure.Seeding;

namespace Tausi.Api.Platform;

/// <summary>
/// Brings the database up to date when the server starts (migrations, then the base seed). If the
/// database is not reachable yet it keeps trying, so the server still starts and reports the
/// problem on /api/health.
/// </summary>
public sealed class DatabaseStartup(IServiceScopeFactory scopes, IConfiguration config, ILogger<DatabaseStartup> log) : BackgroundService
{
    public static readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Database:MigrateOnStartup", true))
        {
            Ready.TrySetResult();
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Migrate(scopes, stoppingToken);
                log.LogInformation("Database is up to date");
                await ShowSetupToken(scopes, log, stoppingToken);
                Ready.TrySetResult();
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError("Database not ready ({Message}); trying again in 10 seconds", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>
    /// A new install (setup mode): prints the setup token in the console and the log, and keeps it in
    /// setup-token.txt in the data folder. The wizard at /setup asks for it.
    /// </summary>
    public static async Task ShowSetupToken(IServiceScopeFactory scopes, ILogger log, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Application.Abstractions.ITausiDb>();
        if (await scope.ServiceProvider.GetRequiredService<Application.Setup.SetupGate>().IsComplete(db, ct)) return;
        var token = await scope.ServiceProvider.GetRequiredService<Application.Setup.SetupFlow>().EnsureToken(ct);
        if (token is null) return;
        var file = scope.ServiceProvider.GetRequiredService<Application.Setup.ISetupTokenFile>().Path;
        log.LogWarning("Tausi POS is not set up yet. Open http://<this server>:<port>/setup and enter the setup token {Token} (also in {File})", token, file);
        Console.WriteLine();
        Console.WriteLine("  ┌──────────────────────────────────────────────┐");
        Console.WriteLine("  │  Tausi POS is not set up yet.                │");
        Console.WriteLine($"  │  Setup token:  {token}                     │");
        Console.WriteLine("  │  Open /setup on this server in a browser.    │");
        Console.WriteLine("  └──────────────────────────────────────────────┘");
        Console.WriteLine($"  The token is also in {file}");
        Console.WriteLine();
    }

    public static async Task Migrate(IServiceScopeFactory scopes, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TausiDbContext>().Database.MigrateAsync(ct);
        await scope.ServiceProvider.GetRequiredService<Seeder>().SeedBase(ct);
        await scope.ServiceProvider.GetRequiredService<Application.Money.LedgerBackfill>().Run(ct);
    }
}
