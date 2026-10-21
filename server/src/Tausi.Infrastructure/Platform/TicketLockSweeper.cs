using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tausi.Application.Tickets;

namespace Tausi.Infrastructure.Platform;

/// <summary>
/// Every few seconds, ends ticket edit locks whose terminal stopped renewing them or whose session
/// ended (including idle logout), so other terminals can edit the ticket again; and expires
/// approval requests left undecided past <c>pos.approvalExpiryMinutes</c>.
/// </summary>
public sealed class TicketLockSweeper(IServiceScopeFactory scopes, TimeProvider clock, ILogger<TicketLockSweeper> log)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        while (await NextTick(timer, stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var released = await scope.ServiceProvider.GetRequiredService<ReleaseTicketLocks>().Stale(stoppingToken);
                if (released > 0) log.LogInformation("Released {Count} stale ticket lock(s)", released);
                var expired = await scope.ServiceProvider.GetRequiredService<Application.Approvals.ExpireApprovals>().Run(stoppingToken);
                if (expired > 0) log.LogInformation("Expired {Count} approval request(s)", expired);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database may be starting or unreachable; the next tick tries again.
                log.LogDebug(ex, "Ticket lock sweep failed");
            }
        }
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
}
