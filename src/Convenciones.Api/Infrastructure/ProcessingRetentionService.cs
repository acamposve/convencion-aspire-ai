using Npgsql;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Removes the AI-derived output for a document without touching the document itself. The document
/// remains visible; only runs, attempts, clauses and classifications are purged.
/// </summary>
public sealed class ProcessingRetentionService(string connectionString)
{
    public async Task<int> PurgeDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
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
