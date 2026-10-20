using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tausi.Application.Abstractions;
using Tausi.Domain.Platform;

namespace Tausi.Infrastructure.Platform;

/// <summary>Hourly clean-up: idempotency keys older than 24 hours, old pairings and approval tokens.</summary>
public sealed class HousekeepingService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<HousekeepingService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await CleanUp(scope.ServiceProvider.GetRequiredService<ITausiDb>(), clock.GetUtcNow(), stoppingToken);
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
}
