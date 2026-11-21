using System.Diagnostics.Metrics;
using Npgsql;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Samples queue depth/age and worker heartbeat age into OpenTelemetry metrics.
/// </summary>
/// <remarks>
/// The health report answers "is it healthy now" on demand; these gauges are the time series an alert
/// rule needs. Sampling happens on a timer rather than inside the gauge callback so a slow database cannot
/// block metric collection. A failed sample increments a counter and leaves the last known values.
/// </remarks>
public sealed class OperationsMetrics(string connectionString, TimeSpan interval, ILogger<OperationsMetrics> logger)
    : BackgroundService
{
    private static readonly Meter Meter = new("Convenciones.Operations");

    private static readonly Counter<long> SampleFailures =
        Meter.CreateCounter<long>("processing.operations.sample_failures");

    private long _queueDepth = -1;
    private long _oldestQueuedAgeSeconds = -1;
    private long _workerHeartbeatAgeSeconds = -1;
    private long _workerHeartbeatPresent;

    public OperationsMetrics(string connectionString, ILogger<OperationsMetrics> logger)
        : this(connectionString, TimeSpan.FromSeconds(30), logger)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Meter.CreateObservableGauge("processing.queue.depth", () => Interlocked.Read(ref _queueDepth));
        Meter.CreateObservableGauge("processing.queue.oldest_age_seconds", () => Interlocked.Read(ref _oldestQueuedAgeSeconds));
        Meter.CreateObservableGauge("processing.worker.heartbeat_age_seconds", () => Interlocked.Read(ref _workerHeartbeatAgeSeconds));
        Meter.CreateObservableGauge("processing.worker.heartbeat_present", () => Interlocked.Read(ref _workerHeartbeatPresent));

        while (!stoppingToken.IsCancellationRequested)
        {
            await SampleAsync(stoppingToken).ConfigureAwait(false);

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

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var queue = new NpgsqlCommand(
                """
                select count(*) filter (where status = 'Queued'::platform.processing_status),
                       extract(epoch from (now() - min(created_at) filter (where status = 'Queued'::platform.processing_status)))
                from platform.processing_run;
                """, connection))
            await using (var reader = await queue.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _queueDepth, reader.GetInt64(0));
                Interlocked.Exchange(
                    ref _oldestQueuedAgeSeconds,
                    reader.IsDBNull(1) ? -1 : (long)reader.GetDouble(1));
            }

            await using (var heartbeat = new NpgsqlCommand(
                "select extract(epoch from (now() - last_seen_at)) from platform.worker_heartbeat where id = 1;",
                connection))
            await using (var reader = await heartbeat.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref _workerHeartbeatAgeSeconds, (long)reader.GetDouble(0));
                    Interlocked.Exchange(ref _workerHeartbeatPresent, 1);
                }
                else
                {
                    Interlocked.Exchange(ref _workerHeartbeatAgeSeconds, -1);
                    Interlocked.Exchange(ref _workerHeartbeatPresent, 0);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SampleFailures.Add(1);
            logger.LogWarning(exception, "Operational metrics sample failed; keeping the previous values.");
        }
    }
}
