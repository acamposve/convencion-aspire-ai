using Convenciones.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Retries durable document object deletions that could not complete during the request.
/// </summary>
/// <remarks>
/// Runs in the API rather than the worker: object removal is a privacy obligation and must not depend on
/// the processing worker being enabled. A failure is logged and retried on the next interval; nothing is
/// thrown out of the loop, and the intent row keeps the state and the last error.
/// </remarks>
public sealed class DocumentDeletionSweeper(
    DocumentDeletionStore store,
    DocumentDeletionOptions options,
    IDocumentStorage storage,
    ILogger<DocumentDeletionSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.IntervalSeconds <= 0)
        {
            logger.LogInformation("Document deletion sweeper is disabled (Documents:DeletionIntervalSeconds <= 0).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, options.IntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await store
                    .SweepOnceAsync(
                        options.BatchSize,
                        TimeSpan.FromSeconds(Math.Max(30, options.LeaseSeconds)),
                        storage,
                        stoppingToken)
                    .ConfigureAwait(false);

                if (processed > 0)
                {
                    logger.LogInformation("Attempted {Count} pending document object deletion(s).", processed);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Document object deletion sweep failed; retrying on the next interval.");
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
