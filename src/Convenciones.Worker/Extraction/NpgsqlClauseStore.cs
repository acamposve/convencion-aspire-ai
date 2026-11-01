using Npgsql;

namespace Convenciones.Worker.Extraction;

public sealed class NpgsqlClauseStore(string connectionString) : IClauseStore
{
    public async Task<StoredDocument?> LoadDocumentAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select d.storage_path, d.file_name
            from platform.processing_run r
            join platform.document d on d.id = r.document_id
            where r.id = @run;
            """, connection);
        command.Parameters.AddWithValue("run", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredDocument(reader.GetString(0), reader.GetString(1))
            : null;
    }

    public async Task UpdateProgressAsync(
        Guid runId,
        string stage,
        int clauseCount,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            update platform.processing_run
            set stage = @stage, clause_count = @count, updated_at = now()
            where id = @run;
            """, connection);

        command.Parameters.AddWithValue("stage", stage);
        command.Parameters.AddWithValue("count", clauseCount);
        command.Parameters.AddWithValue("run", runId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveClausesAsync(
        ProcessingJob job,
        IReadOnlyList<MergedClause> clauses,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A retry must not leave the previous attempt's clauses behind.
        await using (var delete = new NpgsqlCommand(
            "delete from platform.extracted_clause where run_id = @run;", connection, transaction))
        {
            delete.Parameters.AddWithValue("run", job.RunId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var clause in clauses)
        {
            await using var insert = new NpgsqlCommand(
                """
                insert into platform.extracted_clause
                    (run_id, document_id, company_id, ordinal, clause_number, heading, text,
                     page_from, page_to, confidence, needs_review)
                values
                    (@run, @document, @company, @ordinal, @number, @heading, @text,
                     @page_from, @page_to, @confidence, @needs_review);
                """, connection, transaction);

            insert.Parameters.AddWithValue("run", job.RunId);
            insert.Parameters.AddWithValue("document", job.DocumentId);
            insert.Parameters.AddWithValue("company", job.CompanyId);
            insert.Parameters.AddWithValue("ordinal", clause.Ordinal);
            insert.Parameters.AddWithValue("number", (object?)clause.Number ?? DBNull.Value);
            insert.Parameters.AddWithValue("heading", (object?)clause.Heading ?? DBNull.Value);
            insert.Parameters.AddWithValue("text", clause.Text);
            insert.Parameters.AddWithValue("page_from", clause.PageFrom);
            insert.Parameters.AddWithValue("page_to", clause.PageTo);
            insert.Parameters.AddWithValue("confidence", (decimal)clause.Confidence);
            insert.Parameters.AddWithValue("needs_review", clause.NeedsReview);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var run = new NpgsqlCommand(
            """
            update platform.processing_run
            set clause_count = @count, stage = 'ExtractingClauses', updated_at = now()
            where id = @run;
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("count", clauses.Count);
            run.Parameters.AddWithValue("run", job.RunId);
            await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
