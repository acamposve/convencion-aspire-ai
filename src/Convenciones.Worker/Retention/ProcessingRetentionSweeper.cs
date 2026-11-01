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
public sealed class ProcessingRetentionSweeper(
    string connectionString,
    RetentionOptions options,
    ILogger<ProcessingRetentionSweeper> logger) : BackgroundService
{
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var documents = new List<Guid>();
        await using (var select = new NpgsqlCommand(
            """
            select d.id
            from platform.document d
            where d.deleted_at is not null
              and exists (select 1 from platform.processing_run r where r.document_id = d.id)
            order by d.deleted_at
            limit @batch;
            """, connection))
        {
            select.Parameters.AddWithValue("batch", options.BatchSize);
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
            purged += await DeleteAsync(connection, transaction,
                "delete from platform.clause_classification where clause_id in (select id from platform.extracted_clause where document_id = @document);",
                documentId, cancellationToken).ConfigureAwait(false);
            purged += await DeleteAsync(connection, transaction,
                "delete from platform.processing_attempt where run_id in (select id from platform.processing_run where document_id = @document);",
                documentId, cancellationToken).ConfigureAwait(false);
            purged += await DeleteAsync(connection, transaction,
                "delete from platform.extracted_clause where document_id = @document;",
                documentId, cancellationToken).ConfigureAwait(false);
            purged += await DeleteAsync(connection, transaction,
                "delete from platform.processing_run where document_id = @document;",
                documentId, cancellationToken).ConfigureAwait(false);
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
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes)), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task<int> DeleteAsync(
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
