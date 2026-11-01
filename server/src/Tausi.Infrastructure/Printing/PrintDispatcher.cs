using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tausi.Infrastructure.Printing;

/// <summary>Background service that keeps sending queued print jobs (see <see cref="PrintJobProcessor"/>).</summary>
public sealed class PrintDispatcher(IServiceScopeFactory scopes, PrintWakeUp wakeUp, ILogger<PrintDispatcher> log) : BackgroundService
{
    /// <summary>How long it waits with nothing to do; a commit that queues a job wakes it at once (PrintWakeUp).</summary>
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<PrintJobProcessor>();
                if (await processor.ProcessDue(stoppingToken) > 0) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A database outage must not kill the dispatcher; it picks up again when it is back.
                log.LogError(ex, "Print dispatcher loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            try
            {
                await wakeUp.WaitAsync(Idle, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
