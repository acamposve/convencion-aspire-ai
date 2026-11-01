using Npgsql;

namespace Convenciones.Api.Infrastructure;

public sealed record QuotaLimits(int? Documents, int? Pages, long? Tokens);

public sealed record QuotaUsage(
    int Documents,
    int Pages,
    long InputTokens,
    long OutputTokens,
    decimal CostEstimateUsd);

public sealed record QuotaStatus(DateOnly Period, QuotaLimits Limits, QuotaUsage Usage);

public sealed record QuotaDecision(bool Allowed, QuotaStatus Status, string? Detail);

/// <summary>
/// Monthly processing quota. The reservation increments the document counter inside a transaction
/// with <c>FOR UPDATE</c>, so two concurrent requests cannot both take the last slot.
/// </summary>
public sealed class ProcessingQuotaService(string connectionString)
{
    public async Task<QuotaStatus> GetAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var limits = await ReadLimitsAsync(connection, null, companyId, cancellationToken).ConfigureAwait(false);
        var period = await EnsureUsageAsync(connection, null, companyId, cancellationToken).ConfigureAwait(false);
        var usage = await ReadUsageAsync(connection, null, companyId, period, cancellationToken).ConfigureAwait(false);
        return new QuotaStatus(period, limits, usage);
    }

    public async Task<QuotaDecision> TryReserveAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var period = await EnsureUsageAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
        var limits = await ReadLimitsAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
        var usage = await ReadUsageForUpdateAsync(connection, transaction, companyId, period, cancellationToken).ConfigureAwait(false);

        var exceeded = (limits.Documents is { } documents && usage.Documents >= documents)
            || (limits.Pages is { } pages && usage.Pages >= pages)
            || (limits.Tokens is { } tokens && usage.InputTokens + usage.OutputTokens >= tokens);

        if (exceeded)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new QuotaDecision(false, new QuotaStatus(period, limits, usage), "The monthly processing quota is exhausted.");
        }

        await using (var update = new NpgsqlCommand(
            """
            update platform.processing_usage
            set documents = documents + 1, updated_at = now()
            where company_id = @company and period = @period;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("company", companyId);
            update.Parameters.AddWithValue("period", period);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new QuotaDecision(
            true,
            new QuotaStatus(period, limits, usage with { Documents = usage.Documents + 1 }),
            null);
    }

    public async Task SetAsync(
        Guid companyId,
        QuotaLimits limits,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            insert into platform.company_quota (company_id, documents_limit, pages_limit, tokens_limit, updated_at)
            values (@company, @documents, @pages, @tokens, now())
            on conflict (company_id) do update
            set documents_limit = excluded.documents_limit,
                pages_limit = excluded.pages_limit,
                tokens_limit = excluded.tokens_limit,
                updated_at = now();
            """, connection);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("documents", (object?)limits.Documents ?? DBNull.Value);
        command.Parameters.AddWithValue("pages", (object?)limits.Pages ?? DBNull.Value);
        command.Parameters.AddWithValue("tokens", (object?)limits.Tokens ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task<DateOnly> EnsureUsageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand(
            """
            insert into platform.processing_usage (company_id, period)
            values (@company, date_trunc('month', now())::date)
            on conflict (company_id, period) do nothing;
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("company", companyId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var select = new NpgsqlCommand(
            """
            select period from platform.processing_usage
            where company_id = @company and period = date_trunc('month', now())::date;
            """, connection, transaction);
        select.Parameters.AddWithValue("company", companyId);
        return (DateOnly)(await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<QuotaLimits> ReadLimitsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select documents_limit, pages_limit, tokens_limit from platform.company_quota where company_id = @company;",
            connection,
            transaction);
        command.Parameters.AddWithValue("company", companyId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new QuotaLimits(null, null, null);
        }

        return new QuotaLimits(
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static async Task<QuotaUsage> ReadUsageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid companyId,
        DateOnly period,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select documents, pages, input_tokens, output_tokens, cost_estimate_usd
            from platform.processing_usage
            where company_id = @company and period = @period;
            """, connection, transaction);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("period", period);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new QuotaUsage(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetDecimal(4));
    }

    private static async Task<QuotaUsage> ReadUsageForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid companyId,
        DateOnly period,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            select documents, pages, input_tokens, output_tokens, cost_estimate_usd
            from platform.processing_usage
            where company_id = @company and period = @period
            for update;
            """, connection, transaction);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("period", period);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new QuotaUsage(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetDecimal(4));
    }
}
