using System.Collections.Generic;
using System.Linq;
using Npgsql;
using Convenciones.Clauses;
using Convenciones.Worker;

namespace Convenciones.Worker.Extraction;

public sealed class NpgsqlClauseStore(string connectionString) : IClauseStore
{
    public async Task<StoredDocument?> LoadDocumentAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select d.id, d.storage_path, d.file_name, d.deleted_at
            from platform.processing_run r
            left join platform.document d on d.id = r.document_id
            where r.id = @run;
            """, connection);
        command.Parameters.AddWithValue("run", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        if (!reader.IsDBNull(3))
        {
            throw new DocumentDeletedException(reader.GetGuid(0));
        }

        return new StoredDocument(reader.GetString(1), reader.GetString(2));
    }

    /// <summary>
    /// Fenced progress write: one conditional UPDATE that only matches when the run is still Running,
    /// still on this attempt number, and the attempt row belongs to it and is still Running. A stale
    /// worker updates nothing and is told so.
    /// </summary>
    public async Task UpdateProgressAsync(
        ProcessingJob job,
        string stage,
        int clauseCount,
        CancellationToken cancellationToken)
    {
        if (job.AttemptId == Guid.Empty)
        {
            throw new StaleProcessingAttemptException(job.RunId, job.AttemptId);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            update platform.processing_run r
            set stage = @stage, clause_count = @count, updated_at = now()
            where r.id = @run
              and r.status = 'Running'::platform.processing_status
              and r.attempts = @attempt
              and exists (
                  select 1
                  from platform.processing_attempt a
                  where a.id = @attempt_id
                    and a.run_id = r.id
                    and a.attempt_no = @attempt
                    and a.status = 'Running'::platform.processing_status)
            returning r.id;
            """, connection);

        command.Parameters.AddWithValue("stage", stage);
        command.Parameters.AddWithValue("count", clauseCount);
        command.Parameters.AddWithValue("run", job.RunId);
        command.Parameters.AddWithValue("attempt", job.Attempt);
        command.Parameters.AddWithValue("attempt_id", job.AttemptId);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new StaleProcessingAttemptException(job.RunId, job.AttemptId);
        }
    }

    public async Task SaveClausesAsync(
        ProcessingJob job,
        IReadOnlyList<MergedClause> clauses,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockDocumentAsync(connection, transaction, job.DocumentId, cancellationToken).ConfigureAwait(false);
        await EnsureDocumentActiveAsync(connection, transaction, job.DocumentId, cancellationToken).ConfigureAwait(false);
        await EnsureAttemptOwnedAsync(connection, transaction, job, cancellationToken).ConfigureAwait(false);
        await LockRunClausesAsync(connection, transaction, job.RunId, cancellationToken).ConfigureAwait(false);
        await EnsureNoHumanCorrectionsAsync(connection, transaction, job.RunId, cancellationToken).ConfigureAwait(false);

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

    public async Task<IReadOnlyList<StoredClause>> LoadClausesAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select id, ordinal, clause_number, heading, text
            from platform.extracted_clause
            where run_id = @run
            order by ordinal;
            """, connection);
        command.Parameters.AddWithValue("run", runId);

        var clauses = new List<StoredClause>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            clauses.Add(new StoredClause(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        }

        return clauses;
    }

    public async Task<IReadOnlyList<ClauseCategoryInfo>> LoadActiveCategoriesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            select id, key, name, description
            from platform.clause_category
            where is_active
            order by sort_order, name;
            """, connection);

        var categories = new List<ClauseCategoryInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            categories.Add(new ClauseCategoryInfo(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return categories;
    }

    public async Task SaveClassificationsAsync(
        ProcessingJob job,
        IReadOnlyList<ClassificationAssignment> assignments,
        int unclassifiedCount,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockDocumentAsync(connection, transaction, job.DocumentId, cancellationToken).ConfigureAwait(false);
        await EnsureDocumentActiveAsync(connection, transaction, job.DocumentId, cancellationToken).ConfigureAwait(false);
        await EnsureAttemptOwnedAsync(connection, transaction, job, cancellationToken).ConfigureAwait(false);
        await LockRunClausesAsync(connection, transaction, job.RunId, cancellationToken).ConfigureAwait(false);

        await using (var delete = new NpgsqlCommand(
            """
            delete from platform.clause_classification
            where clause_id in (select id from platform.extracted_clause where run_id = @run);
            """, connection, transaction))
        {
            delete.Parameters.AddWithValue("run", job.RunId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var assignment in assignments)
        {
            await using var insert = new NpgsqlCommand(
                """
                insert into platform.clause_classification
                    (clause_id, category_id, company_id, confidence, is_primary, rationale, model, prompt_version)
                values
                    (@clause, @category, @company, @confidence, @primary, @rationale, @model, @prompt);
                """, connection, transaction);

            insert.Parameters.AddWithValue("clause", assignment.ClauseId);
            insert.Parameters.AddWithValue("category", assignment.CategoryId);
            insert.Parameters.AddWithValue("company", job.CompanyId);
            insert.Parameters.AddWithValue("confidence", (decimal)assignment.Confidence);
            insert.Parameters.AddWithValue("primary", assignment.IsPrimary);
            insert.Parameters.AddWithValue("rationale", (object?)assignment.Rationale ?? DBNull.Value);
            insert.Parameters.AddWithValue("model", "azure-foundry");
            insert.Parameters.AddWithValue("prompt", "classification/v1");
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var run = new NpgsqlCommand(
            """
            update platform.processing_run
            set classified_count = @classified,
                unclassified_count = @unclassified,
                stage = 'ClassifyingClauses',
                updated_at = now()
            where id = @run;
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("classified", assignments.Select(item => item.ClauseId).Distinct().Count());
            run.Parameters.AddWithValue("unclassified", unclassifiedCount);
            run.Parameters.AddWithValue("run", job.RunId);
            await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordUsageAsync(
        Guid companyId,
        int pages,
        long inputTokens,
        long outputTokens,
        decimal costEstimateUsd,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            insert into platform.processing_usage
                (company_id, period, pages, input_tokens, output_tokens, cost_estimate_usd, updated_at)
            values
                (@company, date_trunc('month', now())::date, @pages, @input, @output, @cost, now())
            on conflict (company_id, period) do update
            set pages = platform.processing_usage.pages + excluded.pages,
                input_tokens = platform.processing_usage.input_tokens + excluded.input_tokens,
                output_tokens = platform.processing_usage.output_tokens + excluded.output_tokens,
                cost_estimate_usd = platform.processing_usage.cost_estimate_usd + excluded.cost_estimate_usd,
                updated_at = now();
            """, connection);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("pages", pages);
        command.Parameters.AddWithValue("input", inputTokens);
        command.Parameters.AddWithValue("output", outputTokens);
        command.Parameters.AddWithValue("cost", costEstimateUsd);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureNoHumanCorrectionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select exists(
                select 1
                from platform.audit_entry a
                join platform.extracted_clause c on c.id::text = a.entity_id
                where c.run_id = @run
                  and a.entity_type = 'extracted_clause'
                  and a.action = 'clause.classification.corrected');
            """, connection, transaction);
        command.Parameters.AddWithValue("run", runId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
        {
            throw new InvalidOperationException(
                "A human classification correction exists for this run; refusing to discard it.");
        }
    }

    /// <summary>
    /// Serializes AI writes for a run's clauses with the API's per-clause correction locks.
    /// Locks are acquired in ordinal order so concurrent workers cannot form a lock cycle.
    /// </summary>
    private static async Task EnsureAttemptOwnedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingJob job,
        CancellationToken cancellationToken)
    {
        if (job.AttemptId == Guid.Empty)
        {
            throw new StaleProcessingAttemptException(job.RunId, job.AttemptId);
        }

        await using var command = new NpgsqlCommand(
            """
            select r.id
            from platform.processing_run r
            join platform.processing_attempt a on a.run_id = r.id
            where r.id = @run
              and r.status = 'Running'::platform.processing_status
              and r.attempts = @attempt
              and a.id = @attempt_id
              and a.attempt_no = @attempt
              and a.status = 'Running'::platform.processing_status
            for update of r, a;
            """, connection, transaction);
        command.Parameters.AddWithValue("run", job.RunId);
        command.Parameters.AddWithValue("attempt", job.Attempt);
        command.Parameters.AddWithValue("attempt_id", job.AttemptId);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new StaleProcessingAttemptException(job.RunId, job.AttemptId);
        }
    }

    private static async Task EnsureDocumentActiveAsync(
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
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new DocumentDeletedException(documentId);
        }
    }

    private static async Task LockDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var locking = new NpgsqlCommand(
            "select pg_advisory_xact_lock_shared(hashtextextended(@key, 0));",
            connection,
            transaction);
        locking.Parameters.AddWithValue("key", ClassificationLocks.DocumentKey(documentId));
        await locking.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockRunClausesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await LockAsync(
            connection,
            transaction,
            ClassificationLocks.RunKey(runId),
            cancellationToken).ConfigureAwait(false);

        var clauseIds = new List<Guid>();
        await using (var select = new NpgsqlCommand(
            "select id from platform.extracted_clause where run_id = @run order by ordinal;",
            connection,
            transaction))
        {
            select.Parameters.AddWithValue("run", runId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                clauseIds.Add(reader.GetGuid(0));
            }
        }

        foreach (var clauseId in clauseIds)
        {
            await LockAsync(
                connection,
                transaction,
                ClassificationLocks.ClauseKey(clauseId),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var locking = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0));",
            connection,
            transaction);
        locking.Parameters.AddWithValue("key", key);
        await locking.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
