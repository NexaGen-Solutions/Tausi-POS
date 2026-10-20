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

    public static async Task Migrate(IServiceScopeFactory scopes, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TausiDbContext>().Database.MigrateAsync(ct);
        await scope.ServiceProvider.GetRequiredService<Seeder>().SeedBase(ct);
    }
}
