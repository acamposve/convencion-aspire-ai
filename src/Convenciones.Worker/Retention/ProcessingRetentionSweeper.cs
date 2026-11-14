using Convenciones.Clauses;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Convenciones.Worker;

public sealed class RetentionOptions
{
    public int IntervalMinutes { get; set; } = 360;

    public int BatchSize { get; set; } = 100;
}

/// <summary>
/// Periodically removes AI output for documents that the tenant soft-deleted. The document row stays
/// (it is the tenant's record); only runs, attempts, clauses and classifications are purged.
/// </summary>
/// <remarks>
/// Retention belongs to the worker lifecycle. When <see cref="WorkerOptions.Enabled"/> is false the
/// hosted service completes without sweeping, so neither the startup pass nor a later interval runs.
/// A disabled worker must never be assumed to have swept.
/// </remarks>
public sealed class ProcessingRetentionSweeper(
    string connectionString,
    RetentionOptions options,
    WorkerOptions workerOptions,
    ILogger<ProcessingRetentionSweeper> logger) : BackgroundService
{
    /// <summary>
    /// Delay between passes. Production always uses the real timer; tests replace it so the periodic
    /// loop advances on an explicit signal instead of wall-clock time.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        static (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        if (workerOptions.RequireScope && workerOptions.AllowedCompanyId is null)
        {
            throw new InvalidOperationException("Retention cannot run without an authorized company scope.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var documents = new List<Guid>();
        await using (var select = new NpgsqlCommand(
            """
            select d.id
            from platform.document d
            where d.deleted_at is not null
              and (@company is null or d.company_id = @company)
              and (@document is null or d.id = @document)
              and exists (
                  select 1
                  from platform.processing_run r
                  where r.document_id = d.id
                    and (@run is null or r.id = @run)
                  union all
                  select 1
                  from platform.document_text t
                  where t.document_id = d.id
                    and @run is null)
            order by d.deleted_at
            limit @batch;
            """, connection))
        {
            select.Parameters.AddWithValue("batch", options.BatchSize);
            select.Parameters.AddWithValue(
                "company",
                NpgsqlTypes.NpgsqlDbType.Uuid,
                (object?)workerOptions.AllowedCompanyId ?? DBNull.Value);
            select.Parameters.AddWithValue(
                "document",
                NpgsqlTypes.NpgsqlDbType.Uuid,
                (object?)workerOptions.AllowedDocumentId ?? DBNull.Value);
            select.Parameters.AddWithValue(
                "run",
                NpgsqlTypes.NpgsqlDbType.Uuid,
                (object?)workerOptions.AllowedRunId ?? DBNull.Value);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                documents.Add(reader.GetGuid(0));
            }
        }

        var purged = 0;
        foreach (var documentId in documents)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockAsync(connection, transaction, ClassificationLocks.DocumentKey(documentId), cancellationToken)
                .ConfigureAwait(false);
            var runIds = await RunIdsAsync(connection, transaction, documentId, workerOptions.AllowedRunId, cancellationToken).ConfigureAwait(false);
            foreach (var runId in runIds)
            {
                await LockAsync(connection, transaction, ClassificationLocks.RunKey(runId), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (await HasCorrectionsAsync(connection, transaction, documentId, workerOptions.AllowedRunId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                logger.LogWarning(
                    "Skipping retention for document {DocumentId}: human classification corrections exist.",
                    documentId);
                continue;
            }

            purged += await DeleteAsync(connection, transaction,
                "delete from platform.clause_classification where clause_id in (select id from platform.extracted_clause where document_id = @document and (@run is null or run_id = @run));",
                documentId, workerOptions.AllowedRunId, cancellationToken).ConfigureAwait(false);
            purged += await DeleteAsync(connection, transaction,
                "delete from platform.processing_attempt where run_id in (select id from platform.processing_run where document_id = @document and (@run is null or id = @run));",
                documentId, workerOptions.AllowedRunId, cancellationToken).ConfigureAwait(false);
            purged += await DeleteAsync(connection, transaction,
                "delete from platform.extracted_clause where document_id = @document and (@run is null or run_id = @run);",
                documentId, workerOptions.AllowedRunId, cancellationToken).ConfigureAwait(false);
            purged += await DeleteAsync(connection, transaction,
                "delete from platform.processing_run where document_id = @document and (@run is null or id = @run);",
                documentId, workerOptions.AllowedRunId, cancellationToken).ConfigureAwait(false);

            // Extracted text is an AI-derived artefact tied to the document, not to one run. It is
            // removed only when the whole document's AI output is being retired. A run-scoped
            // retention sweep must leave the text for the remaining/current runs.
            if (workerOptions.AllowedRunId is null)
            {
                purged += await DeleteAsync(connection, transaction,
                    "delete from platform.document_text where document_id = @document;",
                    documentId, null, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (purged > 0)
        {
            logger.LogInformation("Purged AI output for {Documents} soft-deleted document(s), {Rows} row(s).", documents.Count, purged);
        }

        return purged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!workerOptions.Enabled)
        {
            logger.LogInformation(
                "Retention sweeper is disabled (Worker:Enabled=false); no retention sweep will run.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The retention sweep failed; it will run again on the next interval.");
            }

            try
            {
                await DelayAsync(
                        TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes)),
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task<List<Guid>> RunIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using var command = new NpgsqlCommand(
            "select id from platform.processing_run where document_id = @document and (@run is null or id = @run) order by id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("document", documentId);
        command.Parameters.AddWithValue("run", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)runId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<bool> HasCorrectionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select exists(
                select 1
                from platform.audit_entry a
                join platform.extracted_clause c on c.id::text = a.entity_id
                where c.document_id = @document
                  and (@run is null or c.run_id = @run)
                  and a.entity_type = 'extracted_clause'
                  and a.action = 'clause.classification.corrected');
            """, connection, transaction);
        command.Parameters.AddWithValue("document", documentId);
        command.Parameters.AddWithValue("run", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)runId ?? DBNull.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> DeleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid documentId,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("document", documentId);
        command.Parameters.AddWithValue("run", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)runId ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
