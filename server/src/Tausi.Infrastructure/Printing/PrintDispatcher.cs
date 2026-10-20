using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tausi.Infrastructure.Printing;

/// <summary>Background service that keeps sending queued print jobs (see <see cref="PrintJobProcessor"/>).</summary>
public sealed class PrintDispatcher(IServiceScopeFactory scopes, ILogger<PrintDispatcher> log) : BackgroundService
{
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
            await Task.Delay(Idle, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
