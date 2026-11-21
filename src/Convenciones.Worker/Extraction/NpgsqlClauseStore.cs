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
        Guid invocationId,
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

        // The period is immutable and only used to pick the advisory lock. The reservation state that drives
        // the accounting is read *after* the lock is held, so a sweeper cannot settle it in between and
        // leave the record applying a transition that no longer matches the row.
        var period = await ResolvePeriodAsync(connection, transaction, invocationId, cancellationToken)
            .ConfigureAwait(false);
        if (period is not null)
        {
            await LockFinancialAsync(connection, transaction, job.CompanyId, period.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        var settlement = await ReadSettlementAsync(connection, transaction, invocationId, cancellationToken)
            .ConfigureAwait(false);

        var inserted = false;
        await using (var insert = new NpgsqlCommand(
            """
            insert into platform.ai_usage_record
                (invocation_id, company_id, document_id, run_id, attempt_id, stage, invocation_no, provider,
                 model, deployment, input_tokens, output_tokens, usage_source, estimated_cost_usd, currency,
                 pricing_version, pricing_input_per_million, pricing_output_per_million, period)
            select @invocation_id, r.company_id, r.document_id, r.id, a.id, @stage, @invocation, @provider,
                   @model, @deployment, @input, @output, @source, @cost, @currency, @pricing_version,
                   @input_price, @output_price, @period
            from platform.processing_run r
            join platform.processing_attempt a on a.id = @attempt and a.run_id = r.id
            where r.id = @run
            on conflict (invocation_id) do nothing
            returning id;
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("invocation_id", invocationId);
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
            insert.Parameters.Add(new NpgsqlParameter("period", NpgsqlTypes.NpgsqlDbType.Date)
            {
                Value = (object?)settlement?.Period ?? DBNull.Value,
            });

            inserted = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }

        if (!inserted)
        {
            // A row already exists for this identity: either an identical replay (no aggregate change) or
            // a conflicting record, which must never be overwritten.
            var same = false;
            await using (var existing = new NpgsqlCommand(
                """
                select run_id, attempt_id, stage, input_tokens, output_tokens, estimated_cost_usd,
                       currency, usage_source
                from platform.ai_usage_record
                where invocation_id = @invocation_id;
                """, connection, transaction))
            {
                existing.Parameters.AddWithValue("invocation_id", invocationId);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Invocation '{invocationId}' was neither inserted nor found; attempt '{job.AttemptId}' does not belong to run '{job.RunId}'.");
                }

                same = reader.GetGuid(0) == job.RunId
                    && reader.GetGuid(1) == job.AttemptId
                    && reader.GetString(2) == stage
                    && (reader.IsDBNull(3) ? (int?)null : (int)reader.GetInt64(3)) == result.InputTokens
                    && (reader.IsDBNull(4) ? (int?)null : (int)reader.GetInt64(4)) == result.OutputTokens
                    && (reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5)) == cost
                    && reader.GetString(6) == pricing.Currency
                    && reader.GetString(7) == (cost is null ? "unavailable" : "provider");
            }

            if (!same)
            {
                throw new InvalidOperationException(
                    $"Invocation '{invocationId}' already exists with different financial data; refusing to overwrite it.");
            }

            // Identical replay: the aggregate must not be incremented a second time.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (settlement is not null && settlement.Status is "reserved" or "submitted" or "outcome_unknown")
        {
            await ApplySettlementAsync(connection, transaction, invocationId, settlement, cost, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (settlement is not null && settlement.Status == "settled_estimated" && cost is not null)
        {
            // The sweeper already recognised the estimate. The late provider figure must correct it rather
            // than be written beside it, which would leave the counters at the estimate forever.
            await ReconcileSettlementAsync(
                    connection, transaction, invocationId, settlement, cost.Value, cancellationToken)
                .ConfigureAwait(false);
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

    private sealed record SettlementContext(
        Guid BudgetId,
        decimal Estimate,
        string Status,
        DateOnly Period,
        decimal? SettledAmount);

    /// <summary>
    /// Returns the period that owns the invocation, used only to select the financial advisory lock. The
    /// state itself is read again under that lock.
    /// </summary>
    private static async Task<DateOnly?> ResolvePeriodAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        await using (var reservation = new NpgsqlCommand(
            """
            select b.period_start
            from platform.ai_budget_reservation r
            join platform.ai_budget b on b.id = r.budget_id
            where r.invocation_id = @invocation;
            """, connection, transaction))
        {
            reservation.Parameters.AddWithValue("invocation", invocationId);
            var value = await reservation.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                return value is DateOnly date ? date : DateOnly.FromDateTime((DateTime)value);
            }
        }

        await using (var shadow = new NpgsqlCommand(
            "select period from platform.ai_budget_shadow where invocation_id = @invocation;",
            connection, transaction))
        {
            shadow.Parameters.AddWithValue("invocation", invocationId);
            var value = await shadow.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is DateOnly period ? period : null;
        }
    }

    /// <summary>
    /// Finds the financial reservation (or shadow row) for an invocation. Returns null for invocations
    /// recorded before the budget model existed, which keep working without settlement.
    /// </summary>
    /// <remarks>
    /// Must be called with the company-period advisory lock held. The row is locked so the state used for
    /// accounting cannot change between this read and the guarded updates.
    /// </remarks>
    private static async Task<SettlementContext?> ReadSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        await using (var reservation = new NpgsqlCommand(
            """
            select r.budget_id, r.estimated_cost, r.status, b.period_start, r.settled_amount
            from platform.ai_budget_reservation r
            join platform.ai_budget b on b.id = r.budget_id
            where r.invocation_id = @invocation
            for update of r;
            """, connection, transaction))
        {
            reservation.Parameters.AddWithValue("invocation", invocationId);
            await using var reader = await reservation.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new SettlementContext(
                    reader.GetGuid(0),
                    reader.GetDecimal(1),
                    reader.GetString(2),
                    DateOnly.FromDateTime(reader.GetDateTime(3)),
                    reader.IsDBNull(4) ? null : reader.GetDecimal(4));
            }
        }

        await using (var shadow = new NpgsqlCommand(
            "select period from platform.ai_budget_shadow where invocation_id = @invocation for update;",
            connection, transaction))
        {
            shadow.Parameters.AddWithValue("invocation", invocationId);
            var period = await shadow.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (period is DateOnly value)
            {
                return new SettlementContext(Guid.Empty, 0m, "shadow", value, null);
            }
        }

        return null;
    }

    private static async Task LockFinancialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        DateOnly period,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(platform.fin_lock_key(@company, @period));", connection, transaction);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.Add(new NpgsqlParameter("period", NpgsqlTypes.NpgsqlDbType.Date) { Value = period });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the reservation against the actual cost in the same transaction as the ledger row.</summary>
    private static async Task ApplySettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid invocationId,
        SettlementContext settlement,
        decimal? cost,
        CancellationToken cancellationToken)
    {
        var amount = cost ?? settlement.Estimate;
        var status = cost is null ? "settled_estimated" : "settled";

        int affected;
        await using (var update = new NpgsqlCommand(
            """
            update platform.ai_budget_reservation
            set status = @status, settled_amount = @amount, settled_at = now(), updated_at = now()
            where invocation_id = @invocation
              and status in ('reserved', 'submitted', 'outcome_unknown');
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("invocation", invocationId);
            update.Parameters.AddWithValue("status", status);
            update.Parameters.Add(new NpgsqlParameter("amount", NpgsqlTypes.NpgsqlDbType.Numeric)
            {
                Value = amount,
            });
            affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (affected != 1)
        {
            // The row is locked and its state was read under the same lock, so this means the transition no
            // longer applies. Abort before touching the counters: no ledger/counter divergence.
            throw new InvalidOperationException(
                $"Reservation '{invocationId}' was not in a settleable state under the financial lock.");
        }

        await using (var budget = new NpgsqlCommand(
            """
            update platform.ai_budget
            set reserved_amount = reserved_amount - @estimate,
                spent_amount    = spent_amount + @amount,
                updated_at      = now()
            where id = @budget;
            """, connection, transaction))
        {
            budget.Parameters.AddWithValue("budget", settlement.BudgetId);
            budget.Parameters.Add(new NpgsqlParameter("estimate", NpgsqlTypes.NpgsqlDbType.Numeric)
            {
                Value = settlement.Estimate,
            });
            budget.Parameters.Add(new NpgsqlParameter("amount", NpgsqlTypes.NpgsqlDbType.Numeric)
            {
                Value = amount,
            });
            await budget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Corrects a reservation that the sweeper settled at its estimate: only the difference between the
    /// actual cost and the recognised estimate moves the budget, and only once.
    /// </summary>
    private static async Task ReconcileSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid invocationId,
        SettlementContext settlement,
        decimal actualCost,
        CancellationToken cancellationToken)
    {
        var previous = settlement.SettledAmount ?? settlement.Estimate;

        int affected;
        await using (var update = new NpgsqlCommand(
            """
            update platform.ai_budget_reservation
            set status = 'reconciled', settled_amount = @amount, reconciled_at = now(), updated_at = now()
            where invocation_id = @invocation and status = 'settled_estimated';
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("invocation", invocationId);
            update.Parameters.Add(new NpgsqlParameter("amount", NpgsqlTypes.NpgsqlDbType.Numeric)
            {
                Value = actualCost,
            });
            affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (affected == 0)
        {
            // Holding the advisory lock and the row lock, this can only mean the row is not the state we
            // read. Abort rather than move a counter that no longer matches it.
            throw new InvalidOperationException(
                $"Reservation '{invocationId}' was not 'settled_estimated' under the financial lock.");
        }

        await using (var budget = new NpgsqlCommand(
            """
            update platform.ai_budget
            set spent_amount = spent_amount + @delta, updated_at = now()
            where id = @budget;
            """, connection, transaction))
        {
            budget.Parameters.AddWithValue("budget", settlement.BudgetId);
            budget.Parameters.Add(new NpgsqlParameter("delta", NpgsqlTypes.NpgsqlDbType.Numeric)
            {
                Value = actualCost - previous,
            });
            await budget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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
