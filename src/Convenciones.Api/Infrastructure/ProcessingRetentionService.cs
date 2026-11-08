using Convenciones.Clauses;
using Npgsql;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Removes the AI-derived output for a document without touching the document itself. The document
/// remains visible; only runs, attempts, clauses and classifications are purged.
/// </summary>
public sealed class ProcessingRetentionService(string connectionString)
{
    public sealed class ClassificationCorrectionsPresentException(Guid documentId)
        : InvalidOperationException($"Document '{documentId}' has human classification corrections.")
    {
        public Guid DocumentId { get; } = documentId;
    }

    public sealed class DocumentDeletedException(Guid documentId)
        : InvalidOperationException($"Document '{documentId}' is deleted.")
    {
        public Guid DocumentId { get; } = documentId;
    }

    public async Task<int> PurgeDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Re-extraction, worker writes and purge all take the document lock first; API reviews
        // take it shared. Re-extraction and purge then take the run lock. This is the single
        // document -> run -> clause hierarchy.
        await LockAsync(connection, transaction, ClassificationLocks.DocumentKey(documentId), cancellationToken)
            .ConfigureAwait(false);

        await using (var active = new NpgsqlCommand(
            "select exists(select 1 from platform.document where id = @document and deleted_at is null);",
            connection,
            transaction))
        {
            active.Parameters.AddWithValue("document", documentId);
            if (await active.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                throw new DocumentDeletedException(documentId);
            }
        }

        var runIds = new List<Guid>();
        await using (var runs = new NpgsqlCommand(
            "select id from platform.processing_run where document_id = @document order by id;",
            connection,
            transaction))
        {
            runs.Parameters.AddWithValue("document", documentId);
            await using var reader = await runs.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                runIds.Add(reader.GetGuid(0));
            }
        }

        foreach (var runId in runIds)
        {
            await LockAsync(connection, transaction, ClassificationLocks.RunKey(runId), cancellationToken)
                .ConfigureAwait(false);
        }

        await using (var corrections = new NpgsqlCommand(
            """
            select exists(
                select 1
                from platform.audit_entry a
                join platform.extracted_clause c on c.id::text = a.entity_id
                where c.document_id = @document
                  and a.entity_type = 'extracted_clause'
                  and a.action = 'clause.classification.corrected');
            """, connection, transaction))
        {
            corrections.Parameters.AddWithValue("document", documentId);
            if (await corrections.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                throw new ClassificationCorrectionsPresentException(documentId);
            }
        }

        var deleted = 0;
        deleted += await ExecuteAsync(
            connection,
            transaction,
            """
            delete from platform.clause_classification
            where clause_id in (select id from platform.extracted_clause where document_id = @document);
            """,
            documentId,
            cancellationToken).ConfigureAwait(false);
        deleted += await ExecuteAsync(
            connection,
            transaction,
            """
            delete from platform.processing_attempt
            where run_id in (select id from platform.processing_run where document_id = @document);
            """,
            documentId,
            cancellationToken).ConfigureAwait(false);
        deleted += await ExecuteAsync(
            connection,
            transaction,
            "delete from platform.extracted_clause where document_id = @document;",
            documentId,
            cancellationToken).ConfigureAwait(false);
        deleted += await ExecuteAsync(
            connection,
            transaction,
            "delete from platform.processing_run where document_id = @document;",
            documentId,
            cancellationToken).ConfigureAwait(false);
        deleted += await ExecuteAsync(
            connection,
            transaction,
            "delete from platform.document_text where document_id = @document;",
            documentId,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
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

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("document", documentId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
