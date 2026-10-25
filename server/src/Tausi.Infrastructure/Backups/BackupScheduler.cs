using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tausi.Application.Abstractions;
using Tausi.Application.Settings;
using Tausi.Application.Setup;

namespace Tausi.Infrastructure.Backups;

/// <summary>
/// The nightly backup: once each business day, at the set time (05:00 business time by default, after
/// closing), or as soon as the server is running after that time if it was off then.
/// </summary>
public sealed class BackupScheduler(
    IServiceScopeFactory scopes, BackupEngine engine, SetupGate setup, IOptions<TausiOptions> options, TimeProvider clock,
    ILogger<BackupScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Backups.Nightly) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ITausiDb>();
                if (!await setup.IsComplete(db, stoppingToken)) continue;
                var now = await scope.ServiceProvider.GetRequiredService<BusinessClock>().Now(stoppingToken);
                if (Due(now, engine.NightlyTime()) is { } day) await engine.RunNightly(day, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The database may be down; the backup itself records and reports its own failures.
                log.LogWarning("Couldn't check whether the nightly backup is due: {Message}", e.Message);
            }
        }
    }

    /// <summary>The business day whose nightly backup is due at this business time, if any.</summary>
    public static DateOnly? Due(DateTimeOffset businessNow, TimeOnly at) =>
        TimeOnly.FromDateTime(businessNow.DateTime) >= at ? DateOnly.FromDateTime(businessNow.DateTime) : null;
}
