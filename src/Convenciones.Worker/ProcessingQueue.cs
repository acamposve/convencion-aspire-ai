using System.Data;
using Convenciones.Clauses;
using Npgsql;
using NpgsqlTypes;

namespace Convenciones.Worker;

/// <summary>
/// PostgreSQL-backed queue for processing runs. No broker: a row is the queue item, and
/// <c>FOR UPDATE SKIP LOCKED</c> gives one claim per worker without blocking the others.
/// </summary>
public sealed class ProcessingQueue(string connectionString, WorkerOptions options)
{
    public Task<ProcessingJob?> ClaimAsync(CancellationToken cancellationToken) =>
        ClaimAsync(null, options.AllowedDocumentId, options.AllowedRunId, options.AllowedCompanyId, cancellationToken);

    public Task<ProcessingJob?> ClaimAsync(
        DateTimeOffset? deadline,
        CancellationToken cancellationToken) =>
        ClaimAsync(deadline, options.AllowedDocumentId, options.AllowedRunId, options.AllowedCompanyId, cancellationToken);

    public async Task<ProcessingJob?> ClaimAsync(
        DateTimeOffset? deadline,
        Guid? allowedDocumentId,
        Guid? allowedRunId,
        Guid? allowedCompanyId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var job = await ClaimRowAsync(
                connection,
                transaction,
                deadline,
                allowedDocumentId,
                allowedRunId,
                allowedCompanyId,
                cancellationToken)
            .ConfigureAwait(false);
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var attemptId = Guid.NewGuid();
        await using (var attempt = new NpgsqlCommand(
            """
            insert into platform.processing_attempt (id, run_id, company_id, attempt_no, status, started_at)
            values (@id, @run, @company, @attempt_no, 'Running'::platform.processing_status, now());
            """, connection, transaction))
        {
            attempt.Parameters.AddWithValue("id", attemptId);
            attempt.Parameters.AddWithValue("run", job.RunId);
            attempt.Parameters.AddWithValue("company", job.CompanyId);
            attempt.Parameters.AddWithValue("attempt_no", job.Attempt);
            await attempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        job = job with { AttemptId = attemptId };

        await AuditAsync(connection, transaction, job, "processing.started", "Queued", "Running", null, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    private async Task<ProcessingJob?> ClaimRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset? deadline,
        Guid? allowedDocumentId,
        Guid? allowedRunId,
        Guid? allowedCompanyId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            with candidate as (
                select id
                from platform.processing_run
                where status = 'Queued'::platform.processing_status
                  and available_at <= now()
                  and (@deadline is null or now() < @deadline)
                  and (@allowed_document_id is null or document_id = @allowed_document_id)
                  and (@allowed_run_id is null or id = @allowed_run_id)
                  and (@allowed_company_id is null or company_id = @allowed_company_id)
                  and exists (
                      select 1
                      from platform.document d
                      where d.id = processing_run.document_id
                        and d.deleted_at is null)
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
              -- Explicit terminal guard: a concurrent delete cancels active runs in its own
              -- transaction, so the row may have stopped being Queued between the candidate scan and
              -- this update. The row lock makes that ordering small; this predicate makes it harmless.
              and r.status = 'Queued'::platform.processing_status
            returning r.id, r.document_id, r.company_id, r.attempts;
            """, connection, transaction);

        command.Parameters.AddWithValue("lease", options.LeaseSeconds);
        command.Parameters.AddWithValue("deadline", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)deadline ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "allowed_document_id",
            NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)allowedDocumentId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "allowed_run_id",
            NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)allowedRunId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "allowed_company_id",
            NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)allowedCompanyId ?? DBNull.Value);

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
        await LockDocumentAsync(connection, transaction, job.DocumentId, cancellationToken).ConfigureAwait(false);

        if (!await DocumentIsActiveAsync(connection, transaction, job.DocumentId, cancellationToken).ConfigureAwait(false))
        {
            await CancelInTransactionAsync(
                    connection,
                    transaction,
                    job,
                    "document_deleted",
                    "The document was deleted while the run was active.",
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var updated = 0;
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
            where id = @run
              and status = 'Running'::platform.processing_status
              and exists (
                  select 1
                  from platform.processing_attempt a
                  where a.id = @attempt_id
                    and a.run_id = @run
                    and a.attempt_no = @attempt
                    and a.status = 'Running'::platform.processing_status);
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("run", job.RunId);
            run.Parameters.AddWithValue("attempt_id", job.AttemptId);
            run.Parameters.AddWithValue("attempt", job.Attempt);
            updated = await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await AuditAsync(connection, transaction, job, "processing.completed", "Running", "Completed", null, cancellationToken)
            .ConfigureAwait(false);
        var attemptClosed = await FinishAttemptAsync(connection, transaction, job, "Completed", null, null, cancellationToken)
            .ConfigureAwait(false);
        if (attemptClosed != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            EnsureAttemptClosedExactlyOnce(attemptClosed, job);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(
        ProcessingJob job,
        string code,
        string message,
        bool retryable,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockDocumentAsync(connection, transaction, job.DocumentId, cancellationToken).ConfigureAwait(false);

        var documentActive = await DocumentIsActiveAsync(connection, transaction, job.DocumentId, cancellationToken)
            .ConfigureAwait(false);
        if (!documentActive)
        {
            if (await CancelInTransactionAsync(
                    connection,
                    transaction,
                    job,
                    "document_deleted",
                    "The document was deleted while the run was active.",
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var retry = retryable && job.Attempt < options.MaxAttempts;
        var backoff = BackoffFor(job.Attempt);

        var updated = 0;
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
            where id = @run
              and status = 'Running'::platform.processing_status
              and exists (
                  select 1
                  from platform.processing_attempt a
                  where a.id = @attempt_id
                    and a.run_id = @run
                    and a.attempt_no = @attempt
                    and a.status = 'Running'::platform.processing_status);
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("retry", retry);
            run.Parameters.AddWithValue("backoff", backoff);
            run.Parameters.AddWithValue("code", code);
            run.Parameters.AddWithValue("message", message);
            run.Parameters.AddWithValue("attempt_id", job.AttemptId);
            run.Parameters.AddWithValue("attempt", job.Attempt);
            run.Parameters.AddWithValue("run", job.RunId);
            updated = await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
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
        var attemptClosed = await FinishAttemptAsync(connection, transaction, job, "Failed", code, message, cancellationToken)
            .ConfigureAwait(false);
        if (attemptClosed != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            EnsureAttemptClosedExactlyOnce(attemptClosed, job);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CancelAsync(ProcessingJob job, CancellationToken cancellationToken) =>
        CancelAsync(job, "cancelled", "The run was cancelled.", cancellationToken);

    public async Task CancelAsync(
        ProcessingJob job,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockDocumentAsync(connection, transaction, job.DocumentId, cancellationToken).ConfigureAwait(false);

        if (await CancelInTransactionAsync(connection, transaction, job, code, message, cancellationToken)
                .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> CancelInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingJob job,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        string? previous;
        await using (var status = new NpgsqlCommand(
            "select status::text from platform.processing_run where id = @run;",
            connection,
            transaction))
        {
            status.Parameters.AddWithValue("run", job.RunId);
            previous = await status.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        if (previous is not ("Running" or "Queued"))
        {
            return false;
        }

        var updated = 0;
        await using (var run = new NpgsqlCommand(
            """
            update platform.processing_run
            set status = 'Cancelled'::platform.processing_status,
                stage = 'Cancelled',
                finished_at = now(),
                lease_expires_at = null,
                error_code = @code,
                error_message = @message,
                updated_at = now()
            where id = @run
              and status = @previous::platform.processing_status
              and exists (
                  select 1
                  from platform.processing_attempt a
                  where a.id = @attempt_id
                    and a.run_id = @run
                    and a.attempt_no = @attempt
                    and a.status = 'Running'::platform.processing_status);
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("code", code);
            run.Parameters.AddWithValue("message", message);
            run.Parameters.AddWithValue("previous", previous);
            run.Parameters.AddWithValue("attempt_id", job.AttemptId);
            run.Parameters.AddWithValue("attempt", job.Attempt);
            run.Parameters.AddWithValue("run", job.RunId);
            updated = await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (updated == 0)
        {
            return false;
        }

        await AuditAsync(
                connection,
                transaction,
                job,
                "processing.cancelled",
                previous,
                "Cancelled",
                code,
                cancellationToken).ConfigureAwait(false);
        var attemptClosed = await FinishAttemptAsync(connection, transaction, job, "Cancelled", code, message, cancellationToken)
            .ConfigureAwait(false);
        EnsureAttemptClosedExactlyOnce(attemptClosed, job);
        return true;
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
        if (options.RequireScope && options.AllowedCompanyId is null)
        {
            throw new InvalidOperationException("Recovery cannot run without an authorized company scope.");
        }

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
                  and (@allowed_company_id is null or company_id = @allowed_company_id)
                  and (@allowed_document_id is null or document_id = @allowed_document_id)
                  and (@allowed_run_id is null or id = @allowed_run_id)
                  and exists (
                      select 1
                      from platform.document d
                      where d.id = processing_run.document_id
                        and d.deleted_at is null)
                returning id, requested_by, attempts
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
                  and (@allowed_company_id is null or company_id = @allowed_company_id)
                  and (@allowed_document_id is null or document_id = @allowed_document_id)
                  and (@allowed_run_id is null or id = @allowed_run_id)
                  and exists (
                      select 1
                      from platform.document d
                      where d.id = processing_run.document_id
                        and d.deleted_at is null)
                returning id, requested_by, attempts
            ), cancelled as (
                update platform.processing_run
                set status = 'Cancelled'::platform.processing_status,
                    stage = 'Cancelled',
                    finished_at = now(),
                    lease_expires_at = null,
                    error_code = 'document_deleted',
                    error_message = 'The document was deleted while the run was active.',
                    updated_at = now()
                where status = 'Running'::platform.processing_status
                  and lease_expires_at < now()
                  and (@allowed_company_id is null or company_id = @allowed_company_id)
                  and (@allowed_document_id is null or document_id = @allowed_document_id)
                  and (@allowed_run_id is null or id = @allowed_run_id)
                  and exists (
                      select 1
                      from platform.document d
                      where d.id = processing_run.document_id
                        and d.deleted_at is not null)
                returning id, requested_by, attempts
            ), attempt_updates as (
                update platform.processing_attempt a
                set status = case
                        when exists (select 1 from requeued r where r.id = a.run_id)
                          or exists (select 1 from failed f where f.id = a.run_id)
                            then 'Failed'::platform.processing_status
                        else 'Cancelled'::platform.processing_status
                    end,
                    error_code = case
                        when exists (select 1 from cancelled c where c.id = a.run_id)
                            then 'document_deleted'
                        else 'worker_interrupted'
                    end,
                    error_message = case
                        when exists (select 1 from cancelled c where c.id = a.run_id)
                            then 'The document was deleted while the run was active.'
                        else 'The worker stopped before finishing the attempt.'
                    end,
                    finished_at = now()
                from (
                    select id, attempts from requeued
                    union all
                    select id, attempts from failed
                    union all
                    select id, attempts from cancelled
                ) x
                where a.run_id = x.id
                  and a.attempt_no = x.attempts
                  and a.status = 'Running'::platform.processing_status
                returning a.id
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
                union all
                select requested_by,
                       'documents',
                       'processing_run',
                       id::text,
                       'processing.cancelled',
                       'Running',
                       'Cancelled',
                       jsonb_build_object('code', 'document_deleted')
                from cancelled
            )
            select (select count(*) from requeued)
                 + (select count(*) from failed)
                 + (select count(*) from cancelled)
                 + (select count(*) from attempt_updates) * 0;
            """, connection, transaction);

        command.Parameters.AddWithValue("max", options.MaxAttempts);
        command.Parameters.AddWithValue(
            "allowed_company_id",
            NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)options.AllowedCompanyId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "allowed_document_id",
            NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)options.AllowedDocumentId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "allowed_run_id",
            NpgsqlTypes.NpgsqlDbType.Uuid,
            (object?)options.AllowedRunId ?? DBNull.Value);
        var recovered = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recovered;
    }
    private static async Task<bool> DocumentIsActiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists(select 1 from platform.document where id = @document and deleted_at is null);",
            connection,
            transaction);
        command.Parameters.AddWithValue("document", documentId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task LockDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue("key", ClassificationLocks.DocumentKey(documentId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> FinishAttemptAsync(
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
            where id = @attempt_id
              and run_id = @run
              and attempt_no = @attempt
              and status = 'Running'::platform.processing_status;
            """, connection, transaction);

        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("code", (object?)code ?? DBNull.Value);
        command.Parameters.AddWithValue("message", (object?)message ?? DBNull.Value);
        command.Parameters.AddWithValue("attempt_id", job.AttemptId);
        command.Parameters.AddWithValue("run", job.RunId);
        command.Parameters.AddWithValue("attempt", job.Attempt);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A run transition and the attempt close must be one outcome: if the run moved to a terminal state,
    /// exactly one attempt row must close. Anything else is an inconsistent transaction and is rolled back
    /// before it can commit an audit entry or a run state that no attempt explains.
    /// </summary>
    private static void EnsureAttemptClosedExactlyOnce(int affected, ProcessingJob job)
    {
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Run '{job.RunId}' changed state but attempt '{job.AttemptId}' was not closed exactly once.");
        }
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
