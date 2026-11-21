using Convenciones.Worker;
using Npgsql;
using NpgsqlTypes;

namespace Convenciones.Worker.Financial;

public enum ReservationMode
{
    Shadow,
    Enforced,
}

/// <summary>The outcome of asking the budget for permission to call the provider.</summary>
public sealed record ReservationDecision(ReservationMode Mode, bool Admitted, Guid? BudgetId, decimal Estimate);

/// <summary>
/// Financial budget accounting for one provider invocation.
/// </summary>
/// <remarks>
/// Every method takes <c>platform.fin_lock_key(company, period)</c> before touching a budget row, so all
/// financial writers for a company-period serialise on the same key. Lock order is advisory ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ budget row
/// ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ reservation row; nothing here takes them in a different order.
///
/// This store is not wired into the processing stages yet: reservations without settlement would hold
/// budget for calls that already finished. Wiring and usage-period attribution land with the settlement
/// work, which is the only place the two can be committed atomically.
/// </remarks>
public sealed class FinancialBudgetStore(string connectionString) : IFinancialBudget
{
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reserves the estimated cost of one invocation. Returns a shadow decision when the company-period
    /// has no budget row or is still in shadow mode; refuses only an enforced budget that cannot cover
    /// the estimate.
    /// </summary>
    public async Task<ReservationDecision> ReserveAsync(
        ProcessingJob job,
        string stage,
        Guid invocationId,
        decimal estimatedCost,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedCost);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var period = await CurrentPeriodAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, job.CompanyId, period, cancellationToken).ConfigureAwait(false);

        var budget = await ReadBudgetAsync(connection, transaction, job.CompanyId, period, cancellationToken)
            .ConfigureAwait(false);

        if (budget is null || budget.Enforcement == "shadow")
        {
            var wouldAdmit = budget is null || budget.Remaining >= estimatedCost;
            await RecordShadowAsync(
                    connection, transaction, job, stage, invocationId, period, estimatedCost, wouldAdmit,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ReservationDecision(ReservationMode.Shadow, true, budget?.Id, estimatedCost);
        }

        var inserted = await InsertReservationAsync(
                connection, transaction, job, stage, invocationId, budget.Id, estimatedCost, cancellationToken)
            .ConfigureAwait(false);

        if (!inserted)
        {
            // Same invocation asking again: one reservation, no second reservation movement.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ReservationDecision(ReservationMode.Enforced, true, budget.Id, estimatedCost);
        }

        var moved = await TryReserveAsync(connection, transaction, budget.Id, estimatedCost, cancellationToken)
            .ConfigureAwait(false);
        if (!moved)
        {
            // Refusal rolls back the reservation insert as well: no partial financial state.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new ReservationDecision(ReservationMode.Enforced, false, null, estimatedCost);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReservationDecision(ReservationMode.Enforced, true, budget.Id, estimatedCost);
    }

    /// <summary>Marks the invocation as submitted before the HTTP request leaves the process.</summary>
    public async Task MarkSubmittedAsync(
        ProcessingJob job,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var period = await ReservationPeriodAsync(connection, transaction, invocationId, cancellationToken)
            .ConfigureAwait(false);

        if (period is null)
        {
            // Shadow invocations carry no budget reservation; the marker still exists so recovery can tell
            // "never submitted" from "possibly billed".
            await using var shadow = new NpgsqlCommand(
                """
                update platform.ai_budget_shadow
                set submitted_at = coalesce(submitted_at, now())
                where invocation_id = @invocation;
                """, connection, transaction);
            shadow.Parameters.AddWithValue("invocation", invocationId);
            await shadow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await LockAsync(connection, transaction, job.CompanyId, period.Value, cancellationToken).ConfigureAwait(false);

        int affected;
        await using (var update = new NpgsqlCommand(
            """
            update platform.ai_budget_reservation
            set status = 'submitted', submitted_at = now(), updated_at = now()
            where invocation_id = @invocation
              and status = 'reserved'
              and exists (
                  select 1
                  from platform.processing_attempt a
                  join platform.processing_run r on r.id = a.run_id
                  where a.id = @attempt and a.status = 'Running' and r.status = 'Running');
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("invocation", invocationId);
            update.Parameters.AddWithValue("attempt", job.AttemptId);
            affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new StaleProcessingAttemptException(job.RunId, job.AttemptId);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles the reservation. A null <paramref name="actualCost"/> means the outcome is unknown and the
    /// estimate is recognised instead: unknown usage is never treated as free.
    /// </summary>
    public async Task SettleAsync(
        Guid companyId,
        Guid invocationId,
        decimal? actualCost,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var period = await ReservationPeriodAsync(connection, transaction, invocationId, cancellationToken)
            .ConfigureAwait(false);
        if (period is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await LockAsync(connection, transaction, companyId, period.Value, cancellationToken).ConfigureAwait(false);

        var reservation = await ReadReservationAsync(connection, transaction, invocationId, cancellationToken)
            .ConfigureAwait(false);
        if (reservation is null || reservation.Status is "settled" or "reconciled" or "settled_estimated" or "released")
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var amount = actualCost ?? reservation.EstimatedCost;
        var status = actualCost is null ? "settled_estimated" : "settled";

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
            update.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric) { Value = amount });
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            budget.Parameters.AddWithValue("budget", reservation.BudgetId);
            budget.Parameters.Add(new NpgsqlParameter("estimate", NpgsqlDbType.Numeric)
            {
                Value = reservation.EstimatedCost,
            });
            budget.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric) { Value = amount });
            await budget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Corrects an estimated settlement to the provider's actual cost by applying only the difference.
    /// Identical repeats are no-ops; different figures raise instead of overwriting provider data.
    /// </summary>
    public async Task ReconcileAsync(
        ProcessingJob job,
        Guid invocationId,
        decimal actualCost,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actualCost);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var period = await ReservationPeriodAsync(connection, transaction, invocationId, cancellationToken)
            .ConfigureAwait(false);
        if (period is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Invocation '{invocationId}' has no financial reservation.");
        }

        await LockAsync(connection, transaction, job.CompanyId, period.Value, cancellationToken).ConfigureAwait(false);

        var reservation = await ReadReservationAsync(connection, transaction, invocationId, cancellationToken)
            .ConfigureAwait(false);
        if (reservation is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Invocation '{invocationId}' has no financial reservation.");
        }

        if (reservation.Status == "reconciled")
        {
            if (reservation.SettledAmount == actualCost)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Invocation '{invocationId}' was already reconciled at {reservation.SettledAmount}; refusing {actualCost}.");
        }

        if (reservation.Status != "settled_estimated")
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Invocation '{invocationId}' is '{reservation.Status}', not 'settled_estimated'; nothing to reconcile.");
        }

        var previous = reservation.SettledAmount
            ?? throw new InvalidOperationException($"Invocation '{invocationId}' has no settled estimate.");

        await using (var update = new NpgsqlCommand(
            """
            update platform.ai_budget_reservation
            set status = 'reconciled', settled_amount = @amount, reconciled_at = now(), updated_at = now()
            where invocation_id = @invocation and status = 'settled_estimated';
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("invocation", invocationId);
            update.Parameters.Add(new NpgsqlParameter("amount", NpgsqlDbType.Numeric) { Value = actualCost });
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var budget = new NpgsqlCommand(
            """
            update platform.ai_budget
            set spent_amount = spent_amount + @delta, updated_at = now()
            where id = @budget;
            """, connection, transaction))
        {
            budget.Parameters.AddWithValue("budget", reservation.BudgetId);
            budget.Parameters.Add(new NpgsqlParameter("delta", NpgsqlDbType.Numeric)
            {
                Value = actualCost - previous,
            });
            await budget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases a reservation that provably never reached the provider. A submitted invocation is never
    /// released on the strength of a timestamp.
    /// </summary>
    public async Task ReleaseAsync(Guid companyId, Guid invocationId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var period = await ReservationPeriodAsync(connection, transaction, invocationId, cancellationToken)
            .ConfigureAwait(false);
        if (period is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await LockAsync(connection, transaction, companyId, period.Value, cancellationToken).ConfigureAwait(false);

        Guid? budgetId = null;
        decimal estimate = 0;
        await using (var update = new NpgsqlCommand(
            """
            update platform.ai_budget_reservation
            set status = 'released', released_at = now(), updated_at = now()
            where invocation_id = @invocation
              and status = 'reserved'
              and submitted_at is null
            returning budget_id, estimated_cost;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("invocation", invocationId);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                budgetId = reader.GetGuid(0);
                estimate = reader.GetDecimal(1);
            }
        }

        if (budgetId is not null)
        {
            await using var budget = new NpgsqlCommand(
                """
                update platform.ai_budget
                set reserved_amount = reserved_amount - @estimate, updated_at = now()
                where id = @budget;
                """, connection, transaction);
            budget.Parameters.AddWithValue("budget", budgetId.Value);
            budget.Parameters.Add(new NpgsqlParameter("estimate", NpgsqlDbType.Numeric) { Value = estimate });
            await budget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles every submitted reservation whose provider response never arrived at its estimate.
    /// Unknown outcomes are charged conservatively; they are never released on the strength of a
    /// timestamp alone.
    /// </summary>
    public async Task<int> ExpireAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var expired = new List<Guid>();

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                select invocation_id
                from platform.ai_budget_reservation
                where company_id = @company
                  and status = 'submitted'
                  and expires_at < now();
                """, connection);
            command.Parameters.AddWithValue("company", companyId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expired.Add(reader.GetGuid(0));
            }
        }

        foreach (var invocationId in expired)
        {
            await SettleAsync(companyId, invocationId, null, cancellationToken).ConfigureAwait(false);
        }

        return expired.Count;
    }

    // ------------------------------------------------------------------ helpers

    private sealed record BudgetRow(Guid Id, string Enforcement, decimal Remaining);

    private sealed record ReservationRow(Guid BudgetId, decimal EstimatedCost, decimal? SettledAmount, string Status);

    private static async Task<DateOnly> CurrentPeriodAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select (date_trunc('month', now()))::date;", connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (DateOnly)value!;
    }

    private static async Task LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        DateOnly period,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(platform.fin_lock_key(@company, @period));", connection, transaction);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.Add(new NpgsqlParameter("period", NpgsqlDbType.Date) { Value = period });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BudgetRow?> ReadBudgetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        DateOnly period,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select id, enforcement, limit_amount + max_overspend - reserved_amount - spent_amount
            from platform.ai_budget
            where company_id = @company and period_start = @period and currency = 'USD'
            for update;
            """, connection, transaction);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.Add(new NpgsqlParameter("period", NpgsqlDbType.Date) { Value = period });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new BudgetRow(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2))
            : null;
    }

    private static async Task<bool> InsertReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingJob job,
        string stage,
        Guid invocationId,
        Guid budgetId,
        decimal estimatedCost,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into platform.ai_budget_reservation
                (invocation_id, budget_id, company_id, run_id, attempt_id, stage, estimated_cost,
                 currency, status, origin, expires_at)
            select @invocation, @budget, r.company_id, r.id, a.id, @stage, @estimate,
                   'USD', 'reserved', 'reservation', now() + @lifetime
            from platform.processing_run r
            join platform.processing_attempt a on a.id = @attempt and a.run_id = r.id
            where r.id = @run
            on conflict (invocation_id) do nothing
            returning invocation_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("invocation", invocationId);
        command.Parameters.AddWithValue("budget", budgetId);
        command.Parameters.AddWithValue("stage", stage);
        command.Parameters.Add(new NpgsqlParameter("estimate", NpgsqlDbType.Numeric) { Value = estimatedCost });
        command.Parameters.Add(new NpgsqlParameter("lifetime", NpgsqlDbType.Interval) { Value = ReservationLifetime });
        command.Parameters.AddWithValue("run", job.RunId);
        command.Parameters.AddWithValue("attempt", job.AttemptId);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> TryReserveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid budgetId,
        decimal estimatedCost,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update platform.ai_budget
            set reserved_amount = reserved_amount + @estimate, updated_at = now()
            where id = @budget
              and reserved_amount + spent_amount + @estimate <= limit_amount + max_overspend
            returning id;
            """, connection, transaction);
        command.Parameters.AddWithValue("budget", budgetId);
        command.Parameters.Add(new NpgsqlParameter("estimate", NpgsqlDbType.Numeric) { Value = estimatedCost });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task RecordShadowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProcessingJob job,
        string stage,
        Guid invocationId,
        DateOnly period,
        decimal estimatedCost,
        bool wouldAdmit,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into platform.ai_budget_shadow
                (invocation_id, company_id, run_id, attempt_id, stage, period, estimated_cost,
                 currency, would_admit)
            select @invocation, r.company_id, r.id, a.id, @stage, @period, @estimate, 'USD', @admit
            from platform.processing_run r
            join platform.processing_attempt a on a.id = @attempt and a.run_id = r.id
            where r.id = @run
            on conflict (invocation_id) do nothing;
            """, connection, transaction);
        command.Parameters.AddWithValue("invocation", invocationId);
        command.Parameters.AddWithValue("stage", stage);
        command.Parameters.Add(new NpgsqlParameter("period", NpgsqlDbType.Date) { Value = period });
        command.Parameters.Add(new NpgsqlParameter("estimate", NpgsqlDbType.Numeric) { Value = estimatedCost });
        command.Parameters.AddWithValue("admit", wouldAdmit);
        command.Parameters.AddWithValue("run", job.RunId);
        command.Parameters.AddWithValue("attempt", job.AttemptId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DateOnly?> ReservationPeriodAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select b.period_start
            from platform.ai_budget_reservation r
            join platform.ai_budget b on b.id = r.budget_id
            where r.invocation_id = @invocation;
            """, connection, transaction);
        command.Parameters.AddWithValue("invocation", invocationId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : (DateOnly)value;
    }

    private static async Task<ReservationRow?> ReadReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select budget_id, estimated_cost, settled_amount, status
            from platform.ai_budget_reservation
            where invocation_id = @invocation
            for update;
            """, connection, transaction);
        command.Parameters.AddWithValue("invocation", invocationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ReservationRow(
                reader.GetGuid(0),
                reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.GetString(3))
            : null;
    }
}
