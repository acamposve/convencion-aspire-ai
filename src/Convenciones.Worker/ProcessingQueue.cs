using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace Convenciones.Worker;

/// <summary>
/// PostgreSQL-backed queue for processing runs. No broker: a row is the queue item, and
/// <c>FOR UPDATE SKIP LOCKED</c> gives one claim per worker without blocking the others.
/// </summary>
public sealed class ProcessingQueue(string connectionString, WorkerOptions options)
{
    public async Task<ProcessingJob?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var job = await ClaimRowAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await using (var attempt = new NpgsqlCommand(
            """
            insert into platform.processing_attempt (run_id, company_id, attempt_no, status, started_at)
            values (@run, @company, @attempt_no, 'Running'::platform.processing_status, now());
            """, connection, transaction))
        {
            attempt.Parameters.AddWithValue("run", job.RunId);
            attempt.Parameters.AddWithValue("company", job.CompanyId);
            attempt.Parameters.AddWithValue("attempt_no", job.Attempt);
            await attempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditAsync(connection, transaction, job, "processing.started", "Queued", "Running", null, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    private async Task<ProcessingJob?> ClaimRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            with candidate as (
                select id
                from platform.processing_run
                where status = 'Queued'::platform.processing_status
                  and available_at <= now()
                order by available_at, created_at
                for update skip locked
                limit 1
            )
            update platform.processing_run r
            set status = 'Running'::platform.processing_status,
                stage = 'Preparing',
                started_at = now(),
                lease_expires_at = now() + make_interval(secs => @lease),
                attempts = r.attempts + 1,
                updated_at = now()
            from candidate c
            where r.id = c.id
            returning r.id, r.document_id, r.company_id, r.attempts;
            """, connection, transaction);

        command.Parameters.AddWithValue("lease", options.LeaseSeconds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProcessingJob(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetInt32(3));
    }

    public async Task CompleteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var run = new NpgsqlCommand(
            """
            update platform.processing_run
            set status = 'Completed'::platform.processing_status,
                stage = 'Completed',
                finished_at = now(),
                lease_expires_at = null,
                error_code = null,
                error_message = null,
                updated_at = now()
            where id = @run;
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("run", job.RunId);
            await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditAsync(connection, transaction, job, "processing.completed", "Running", "Completed", null, cancellationToken)
            .ConfigureAwait(false);
        await FinishAttemptAsync(connection, transaction, job, "Completed", null, null, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(
        ProcessingJob job,
        string code,
        string message,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var retry = retryable && job.Attempt < options.MaxAttempts;
        var backoff = BackoffFor(job.Attempt);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var run = new NpgsqlCommand(
            """
            update platform.processing_run
            set status = case when @retry then 'Queued'::platform.processing_status
                              else 'Failed'::platform.processing_status end,
                stage = case when @retry then 'Queued' else 'Failed' end,
                available_at = case when @retry then now() + make_interval(secs => @backoff) else available_at end,
                lease_expires_at = null,
                finished_at = case when @retry then null else now() end,
                error_code = @code,
                error_message = @message,
                updated_at = now()
            where id = @run;
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("retry", retry);
            run.Parameters.AddWithValue("backoff", backoff);
            run.Parameters.AddWithValue("code", code);
            run.Parameters.AddWithValue("message", message);
            run.Parameters.AddWithValue("run", job.RunId);
            await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditAsync(
                connection,
                transaction,
                job,
                retry ? "processing.retried" : "processing.failed",
                "Running",
                retry ? "Queued" : "Failed",
                code,
                cancellationToken).ConfigureAwait(false);
        await FinishAttemptAsync(connection, transaction, job, "Failed", code, message, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var run = new NpgsqlCommand(
            """
            update platform.processing_run
            set status = 'Cancelled'::platform.processing_status,
                stage = 'Cancelled',
                finished_at = now(),
                lease_expires_at = null,
                updated_at = now()
            where id = @run;
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("run", job.RunId);
            await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AuditAsync(
                connection,
                transaction,
                job,
                "processing.cancelled",
                "Running",
                "Cancelled",
                "cancelled",
                cancellationToken).ConfigureAwait(false);
        await FinishAttemptAsync(connection, transaction, job, "Cancelled", "cancelled", "The run was cancelled.", cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsCancelledAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "select status = 'Cancelled'::platform.processing_status from platform.processing_run where id = @id;",
            connection);
        command.Parameters.AddWithValue("id", runId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task AuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingJob job,
        string action,
        string fromState,
        string toState,
        string? code,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into platform.audit_entry
                (actor_id, module, entity_type, entity_id, action, from_state, to_state, changes)
            select requested_by,
                   'documents',
                   'processing_run',
                   id::text,
                   @action,
                   @from_state,
                   @to_state,
                   case when @code::text is null then null else jsonb_build_object('code', @code::text) end
            from platform.processing_run
            where id = @run;
            """, connection, transaction);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("from_state", fromState);
        command.Parameters.AddWithValue("to_state", toState);
        command.Parameters.AddWithValue("code", (object?)code ?? DBNull.Value);
        command.Parameters.AddWithValue("run", job.RunId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Requeues runs whose worker died, or fails them when the attempt limit is exhausted.</summary>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            with requeued as (
                update platform.processing_run
                set status = 'Queued'::platform.processing_status,
                    stage = 'Queued',
                    available_at = now(),
                    lease_expires_at = null,
                    error_code = 'worker_interrupted',
                    error_message = 'The worker stopped before finishing the run.',
                    updated_at = now()
                where status = 'Running'::platform.processing_status
                  and lease_expires_at < now()
                  and attempts < @max
                returning id, requested_by
            ), failed as (
                update platform.processing_run
                set status = 'Failed'::platform.processing_status,
                    stage = 'Failed',
                    finished_at = now(),
                    lease_expires_at = null,
                    error_code = 'worker_interrupted',
                    error_message = 'The run exhausted its attempts after a worker interruption.',
                    updated_at = now()
                where status = 'Running'::platform.processing_status
                  and lease_expires_at < now()
                  and attempts >= @max
                returning id, requested_by
            ), audited as (
                insert into platform.audit_entry
                    (actor_id, module, entity_type, entity_id, action, from_state, to_state, changes)
                select requested_by,
                       'documents',
                       'processing_run',
                       id::text,
                       'processing.recovered',
                       'Running',
                       'Queued',
                       jsonb_build_object('code', 'worker_interrupted')
                from requeued
                union all
                select requested_by,
                       'documents',
                       'processing_run',
                       id::text,
                       'processing.failed',
                       'Running',
                       'Failed',
                       jsonb_build_object('code', 'worker_interrupted')
                from failed
            )
            select (select count(*) from requeued) + (select count(*) from failed);
            """, connection, transaction);

        command.Parameters.AddWithValue("max", options.MaxAttempts);
        var recovered = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recovered;
    }
    private async Task FinishAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingJob job,
        string status,
        string? code,
        string? message,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update platform.processing_attempt
            set status = @status::platform.processing_status,
                error_code = @code,
                error_message = @message,
                finished_at = now()
            where run_id = @run and attempt_no = @attempt;
            """, connection, transaction);

        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("code", (object?)code ?? DBNull.Value);
        command.Parameters.AddWithValue("message", (object?)message ?? DBNull.Value);
        command.Parameters.AddWithValue("run", job.RunId);
        command.Parameters.AddWithValue("attempt", job.Attempt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private int BackoffFor(int attempt)
    {
        if (options.BackoffSeconds.Length == 0)
        {
            return 30;
        }

        return options.BackoffSeconds[Math.Clamp(attempt - 1, 0, options.BackoffSeconds.Length - 1)];
    }
}
