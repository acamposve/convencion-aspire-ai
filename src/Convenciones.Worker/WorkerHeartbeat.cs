using Npgsql;

namespace Convenciones.Worker;

/// <summary>
/// Writes the worker liveness row the API reports through <c>GET /health/ops</c>.
/// </summary>
/// <remarks>
/// The row is updated on startup (including when the worker is disabled) and on every poll, so a stopped
/// worker becomes visible as an ageing timestamp even when the process was never intended to process.
/// </remarks>
public sealed class WorkerHeartbeat(string connectionString)
{
    public async Task TouchAsync(bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            insert into platform.worker_heartbeat (id, last_seen_at, enabled, updated_at)
            values (1, now(), @enabled, now())
            on conflict (id) do update
            set last_seen_at = now(),
                enabled = excluded.enabled,
                updated_at = now();
            """, connection);
        command.Parameters.AddWithValue("enabled", enabled);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
