using Npgsql;

namespace Convenciones.Api.Infrastructure;

/// <summary>Operational thresholds. Both are required configuration; unset means Unknown, never a guess.</summary>
public sealed record OperationsOptions(int? QueueMaxAgeSeconds, int? WorkerHeartbeatMaxAgeSeconds);

public enum OperationsStatus
{
    Healthy,
    Degraded,
    Unhealthy,
    Unknown,
}

public sealed record OperationsSignal(
    string Name,
    OperationsStatus Status,
    string Description,
    IReadOnlyDictionary<string, object> Data);

/// <summary>Reports the age of the oldest queued run and the queue depth.</summary>
/// <remarks>
/// The query reads only timestamps and statuses. An empty queue is Healthy. This is an operational signal:
/// it never makes the API unready, because a backlog is a processing problem, not a reason to stop serving.
/// </remarks>
public sealed class QueueBacklogProbe(string connectionString, OperationsOptions options)
{
    public async Task<OperationsSignal> CheckAsync(CancellationToken cancellationToken)
    {
        if (options.QueueMaxAgeSeconds is not { } maxAgeSeconds)
        {
            return new OperationsSignal(
                "queue", OperationsStatus.Unknown,
                "Operations:QueueMaxAgeSeconds is not configured; the queue age cannot be judged.",
                new Dictionary<string, object>());
        }

        try
        {
            long queued;
            long running;
            double? oldestAgeSeconds;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                select count(*) filter (where status = 'Queued'::platform.processing_status),
                       count(*) filter (where status = 'Running'::platform.processing_status),
                       extract(epoch from (now() - min(created_at) filter (where status = 'Queued'::platform.processing_status)))
                from platform.processing_run;
                """, connection);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            queued = reader.GetInt64(0);
            running = reader.GetInt64(1);
            oldestAgeSeconds = reader.IsDBNull(2) ? null : reader.GetDouble(2);

            var data = new Dictionary<string, object>
            {
                ["queued"] = queued,
                ["running"] = running,
                ["oldestAgeSeconds"] = oldestAgeSeconds is null ? 0 : (long)oldestAgeSeconds.Value,
                ["thresholdSeconds"] = maxAgeSeconds,
                ["queueEmpty"] = queued == 0,
            };

            if (queued == 0 || oldestAgeSeconds is null)
            {
                return new OperationsSignal("queue", OperationsStatus.Healthy, "The queue is empty.", data);
            }

            return oldestAgeSeconds <= maxAgeSeconds
                ? new OperationsSignal(
                    "queue", OperationsStatus.Healthy,
                    "The oldest queued run is within the configured age.", data)
                : new OperationsSignal(
                    "queue", OperationsStatus.Degraded,
                    $"The oldest queued run is {oldestAgeSeconds:F0}s old; the configured maximum is {maxAgeSeconds}s.",
                    data);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new OperationsSignal(
                "queue", OperationsStatus.Unhealthy,
                "The queue could not be read: " + exception.Message,
                new Dictionary<string, object> { ["thresholdSeconds"] = maxAgeSeconds });
        }
    }
}

/// <summary>Reports how long ago the worker last wrote its heartbeat.</summary>
public sealed class WorkerHeartbeatProbe(string connectionString, OperationsOptions options)
{
    public async Task<OperationsSignal> CheckAsync(CancellationToken cancellationToken)
    {
        if (options.WorkerHeartbeatMaxAgeSeconds is not { } maxAgeSeconds)
        {
            return new OperationsSignal(
                "worker", OperationsStatus.Unknown,
                "Operations:WorkerHeartbeatMaxAgeSeconds is not configured; the heartbeat age cannot be judged.",
                new Dictionary<string, object>());
        }

        try
        {
            double ageSeconds;
            bool enabled;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                select extract(epoch from (now() - last_seen_at)), enabled
                from platform.worker_heartbeat
                where id = 1;
                """, connection);

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new OperationsSignal(
                        "worker", OperationsStatus.Unknown,
                        "The worker has never written a heartbeat.",
                        new Dictionary<string, object> { ["thresholdSeconds"] = maxAgeSeconds });
                }

                ageSeconds = reader.GetDouble(0);
                enabled = reader.GetBoolean(1);
            }

            var data = new Dictionary<string, object>
            {
                ["ageSeconds"] = (long)ageSeconds,
                ["thresholdSeconds"] = maxAgeSeconds,
                ["enabled"] = enabled,
            };

            if (ageSeconds <= maxAgeSeconds)
            {
                return new OperationsSignal(
                    "worker", OperationsStatus.Healthy,
                    enabled ? "The worker heartbeat is current." : "The worker is disabled and its heartbeat is current.",
                    data);
            }

            return new OperationsSignal(
                "worker", OperationsStatus.Degraded,
                $"The worker heartbeat is {ageSeconds:F0}s old; the configured maximum is {maxAgeSeconds}s.",
                data);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new OperationsSignal(
                "worker", OperationsStatus.Unhealthy,
                "The worker heartbeat could not be read: " + exception.Message,
                new Dictionary<string, object> { ["thresholdSeconds"] = maxAgeSeconds });
        }
    }
}
