using System.Collections.Generic;
using System.Linq;
using Npgsql;
using Convenciones.Ai;
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

    /// <summary>
    /// Persists one billable provider invocation and its aggregate in a single transaction. The tenant
    /// and document are taken from the run row, never from the caller, so a record cannot be attributed
    /// to the wrong company.
    /// </summary>
    public async Task RecordInvocationAsync(
        ProcessingJob job,
        string stage,
        int invocationNo,
        AiCompletionResult result,
        int pages,
        AiPricing pricing,
        CancellationToken cancellationToken)
    {
        var cost = pricing.EstimateCost(result.InputTokens, result.OutputTokens);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var inserted = false;
        await using (var insert = new NpgsqlCommand(
            """
            insert into platform.ai_usage_record
                (company_id, document_id, run_id, attempt_id, stage, invocation_no, provider, model,
                 deployment, input_tokens, output_tokens, usage_source, estimated_cost_usd, currency,
                 pricing_version, pricing_input_per_million, pricing_output_per_million)
            select r.company_id, r.document_id, r.id, a.id, @stage, @invocation, @provider, @model,
                   @deployment, @input, @output, @source, @cost, @currency, @pricing_version,
                   @input_price, @output_price
            from platform.processing_run r
            join platform.processing_attempt a on a.id = @attempt and a.run_id = r.id
            where r.id = @run
            on conflict (attempt_id, stage, invocation_no) do nothing
            returning id;
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("stage", stage);
            insert.Parameters.AddWithValue("invocation", invocationNo);
            insert.Parameters.AddWithValue("provider", result.Provider);
            insert.Parameters.AddWithValue("model", (object?)result.Model ?? DBNull.Value);
            insert.Parameters.AddWithValue("deployment", (object?)result.Deployment ?? DBNull.Value);
            insert.Parameters.Add(new NpgsqlParameter("input", NpgsqlTypes.NpgsqlDbType.Bigint)
            {
                Value = (object?)result.InputTokens ?? DBNull.Value,
            });
            insert.Parameters.Add(new NpgsqlParameter("output", NpgsqlTypes.NpgsqlDbType.Bigint)
            {
                Value = (object?)result.OutputTokens ?? DBNull.Value,
            });
            insert.Parameters.AddWithValue("source", cost is null ? "unavailable" : "provider");
            insert.Parameters.Add(new NpgsqlParameter("cost", NpgsqlTypes.NpgsqlDbType.Numeric)
            {
                Value = (object?)cost ?? DBNull.Value,
            });
            insert.Parameters.AddWithValue("currency", pricing.Currency);
            insert.Parameters.AddWithValue("pricing_version", pricing.Version);
            insert.Parameters.AddWithValue("input_price", pricing.InputPerMillion);
            insert.Parameters.AddWithValue("output_price", pricing.OutputPerMillion);
            insert.Parameters.AddWithValue("run", job.RunId);
            insert.Parameters.AddWithValue("attempt", job.AttemptId);

            inserted = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }

        if (!inserted)
        {
            // Either this invocation is already recorded (replay) or the attempt is not the run's.
            await using var existing = new NpgsqlCommand(
                """
                select 1 from platform.ai_usage_record
                where attempt_id = @attempt and stage = @stage and invocation_no = @invocation;
                """, connection, transaction);
            existing.Parameters.AddWithValue("attempt", job.AttemptId);
            existing.Parameters.AddWithValue("stage", stage);
            existing.Parameters.AddWithValue("invocation", invocationNo);

            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException(
                    $"Attempt '{job.AttemptId}' does not belong to run '{job.RunId}'; usage was not recorded.");
            }

            // Already recorded: do not touch the aggregate again, or a replay would double-count it.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (var aggregate = new NpgsqlCommand(
            """
            insert into platform.processing_usage
                (company_id, period, pages, input_tokens, output_tokens, cost_estimate_usd, updated_at)
            select r.company_id,
                   date_trunc('month', now())::date,
                   @pages,
                   coalesce(@input, 0),
                   coalesce(@output, 0),
                   coalesce(@cost, 0),
                   now()
            from platform.processing_run r
            where r.id = @run
            on conflict (company_id, period) do update
            set pages = platform.processing_usage.pages + excluded.pages,
                input_tokens = platform.processing_usage.input_tokens + excluded.input_tokens,
                output_tokens = platform.processing_usage.output_tokens + excluded.output_tokens,
                cost_estimate_usd = platform.processing_usage.cost_estimate_usd + excluded.cost_estimate_usd,
                updated_at = now();
            """, connection, transaction))
        {
            aggregate.Parameters.AddWithValue("pages", pages);
            aggregate.Parameters.Add(new NpgsqlParameter("input", NpgsqlTypes.NpgsqlDbType.Bigint)
            {
                Value = (object?)result.InputTokens ?? DBNull.Value,
            });
            aggregate.Parameters.Add(new NpgsqlParameter("output", NpgsqlTypes.NpgsqlDbType.Bigint)
            {
                Value = (object?)result.OutputTokens ?? DBNull.Value,
            });
            aggregate.Parameters.Add(new NpgsqlParameter("cost", NpgsqlTypes.NpgsqlDbType.Numeric)
            {
                Value = (object?)cost ?? DBNull.Value,
            });
            aggregate.Parameters.AddWithValue("run", job.RunId);
            await aggregate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
