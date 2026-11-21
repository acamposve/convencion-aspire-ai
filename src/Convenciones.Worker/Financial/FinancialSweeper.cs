using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Convenciones.Worker.Financial;

/// <summary>
/// Settles submitted financial reservations whose provider response never arrived. Unknown outcomes are
/// recognised at their estimate; they are never released on the strength of a timestamp alone.
/// </summary>
/// <remarks>
/// Runs only while the worker is enabled: a disabled worker must not touch financial state either.
/// </remarks>
public sealed class FinancialSweeper(
    FinancialBudgetStore store,
    WorkerOptions options,
    ILogger<FinancialSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, options.SweepIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.Enabled)
            {
                try
                {
                    var settled = await store.SweepAsync(stoppingToken).ConfigureAwait(false);
                    if (settled > 0)
                    {
                        logger.LogInformation(
                            "Settled {Count} invocation(s) at their estimates after the provider response never arrived.",
                            settled);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Financial sweep failed; retrying on the next interval.");
                }
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
