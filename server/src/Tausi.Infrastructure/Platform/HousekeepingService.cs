using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tausi.Application.Abstractions;
using Tausi.Domain.Platform;

namespace Tausi.Infrastructure.Platform;

/// <summary>
/// Hourly clean-up: idempotency keys older than 24 hours, old pairings and approval tokens. Once a
/// day (from start-up, then every 24 hours) it also clears the text of slips printed longer ago
/// than <c>print.keepSlipTextDays</c>.
/// </summary>
public sealed class HousekeepingService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<HousekeepingService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        var slipsCleared = DateTimeOffset.MinValue;
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ITausiDb>();
                var now = clock.GetUtcNow();
                await CleanUp(db, now, stoppingToken);
                if (now - slipsCleared >= TimeSpan.FromHours(24))
                {
                    var cleared = await ClearOldSlipText(db, now, stoppingToken);
                    if (cleared > 0) log.LogInformation("Cleared the text of {Count} old slips", cleared);
                    slipsCleared = now;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Housekeeping failed; will try again next hour");
            }
        } while (await NextTick(timer, stoppingToken));
    }

    private static async Task<bool> NextTick(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public static async Task CleanUp(ITausiDb db, DateTimeOffset now, CancellationToken ct)
    {
        var keysBefore = now - IdempotencyRecord.Lifetime;
        await db.IdempotencyRecords.Where(r => r.CreatedAt < keysBefore).ExecuteDeleteAsync(ct);
        var dayAgo = now.AddDays(-1);
        await db.TerminalPairings.Where(p => p.ExpiresAt < dayAgo).ExecuteDeleteAsync(ct);
        await db.ApprovalGrants.Where(g => g.ExpiresAt < dayAgo).ExecuteDeleteAsync(ct);
        await db.PinFailures.Where(f => f.At < dayAgo).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Clears what old slips printed (their text and bytes) after <c>print.keepSlipTextDays</c>; the
    /// jobs stay, so the ticket detail still lists every print. Jobs still queued are left alone.
    /// </summary>
    public static async Task<int> ClearOldSlipText(ITausiDb db, DateTimeOffset now, CancellationToken ct)
    {
        var days = (await Application.Settings.SettingsRegistry.Load(db, ct)).Int(Application.Settings.SettingsRegistry.PrintKeepSlipTextDays);
        var before = now.AddDays(-days);
        return await db.PrintJobs
            .Where(j => j.CreatedAt < before && j.SlipClearedAt == null && j.Status != Domain.Printing.PrintJobStatus.Queued)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.TextPreview, "").SetProperty(j => j.Content, Array.Empty<byte>())
                .SetProperty(j => j.SlipClearedAt, now), ct);
    }
}
