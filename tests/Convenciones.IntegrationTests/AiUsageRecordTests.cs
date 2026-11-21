using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Ai;
using Convenciones.Worker;
using Convenciones.Worker.Extraction;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-005: each billable provider invocation is persisted with run/attempt/model/pricing attribution,
/// and the aggregate cannot be counted twice.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AiUsageRecordTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<(Guid Company, Guid User, Guid Document)> _created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (company, user, document) in _created)
        {
            await ExecuteAsync($"delete from platform.ai_usage_record where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.processing_run where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.processing_usage where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.document where id = '{document}';");
            await ExecuteAsync($"delete from platform.user_account where id = '{user}';");
            await ExecuteAsync($"delete from app.company where id = '{company}';");
        }
    }

    [Fact]
    public async Task Provider_usage_is_recorded_with_full_attribution()
    {
        var (job, _) = await CreateClaimedJobAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var pricing = new AiPricing("test-v1", "USD", 0.61m, 1.82m);

        await store.RecordInvocationAsync(
            job, "extraction", Guid.NewGuid(), 1, Result(1_000, 500, "mistral-large", "claude-deployment"), pages: 3, pricing,
            CancellationToken.None);

        var row = await ReadSingleAsync(job);
        row.Company.ShouldBe(job.CompanyId, "the tenant is taken from the run, not the caller");
        row.Document.ShouldBe(job.DocumentId);
        row.Attempt.ShouldBe(job.AttemptId);
        row.Stage.ShouldBe("extraction");
        row.Invocation.ShouldBe(1);
        row.Provider.ShouldBe("azure-foundry");
        row.Model.ShouldBe("mistral-large");
        row.Deployment.ShouldBe("claude-deployment");
        row.InputTokens.ShouldBe(1_000);
        row.OutputTokens.ShouldBe(500);
        row.UsageSource.ShouldBe("provider");
        row.EstimatedCost.ShouldBe(0.00152m);
        row.PricingVersion.ShouldBe("test-v1");
        row.PagesInAggregate.ShouldBe(3);
    }

    [Fact]
    public async Task Unreported_usage_is_stored_as_unavailable_without_a_cost()
    {
        var (job, _) = await CreateClaimedJobAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);

        await store.RecordInvocationAsync(
            job, "extraction", Guid.NewGuid(), 1, Result(null, null, "m", "d"), pages: 0,
            new AiPricing("test-v1", "USD", 0.61m, 1.82m), CancellationToken.None);

        var row = await ReadSingleAsync(job);
        row.UsageSource.ShouldBe("unavailable");
        row.InputTokens.ShouldBeNull();
        row.EstimatedCost.ShouldBeNull("an unreported charge must not be presented as zero");
        row.AggregateInput.ShouldBe(0);
    }

    [Fact]
    public async Task Replaying_the_same_invocation_does_not_duplicate_the_charge()
    {
        var (job, _) = await CreateClaimedJobAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var pricing = new AiPricing("test-v1", "USD", 0.61m, 1.82m);
        var invocationId = Guid.NewGuid();

        await store.RecordInvocationAsync(job, "extraction", invocationId, 1, Result(1_000, 500, "m", "d"), 2, pricing, CancellationToken.None);
        await store.RecordInvocationAsync(job, "extraction", invocationId, 1, Result(1_000, 500, "m", "d"), 2, pricing, CancellationToken.None);

        (await RecordCountAsync(job)).ShouldBe(1);
        var aggregate = await AggregateAsync(job.CompanyId);
        aggregate.InputTokens.ShouldBe(1_000, "the aggregate must not be incremented twice");
        aggregate.Pages.ShouldBe(2);
    }

    [Fact]
    public async Task Two_invocations_in_one_attempt_are_both_recorded()
    {
        var (job, _) = await CreateClaimedJobAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var pricing = new AiPricing("test-v1", "USD", 0.61m, 1.82m);

        await store.RecordInvocationAsync(job, "extraction", Guid.NewGuid(), 1, Result(1_000, 500, "m", "d"), 2, pricing, CancellationToken.None);
        await store.RecordInvocationAsync(job, "extraction", Guid.NewGuid(), 2, Result(2_000, 1_000, "m", "d"), 0, pricing, CancellationToken.None);

        (await RecordCountAsync(job)).ShouldBe(2);
        var aggregate = await AggregateAsync(job.CompanyId);
        aggregate.InputTokens.ShouldBe(3_000);
        aggregate.Pages.ShouldBe(2, "pages are counted once per run, not per invocation");
    }

    [Fact]
    public async Task Pricing_version_and_cost_are_preserved_per_record()
    {
        var (job, _) = await CreateClaimedJobAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);

        await store.RecordInvocationAsync(
            job, "extraction", Guid.NewGuid(), 1, Result(1_000_000, 0, "m", "d"), 0,
            new AiPricing("price-a", "USD", 0.61m, 1.82m), CancellationToken.None);
        await store.RecordInvocationAsync(
            job, "extraction", Guid.NewGuid(), 2, Result(1_000_000, 0, "m", "d"), 0,
            new AiPricing("price-b", "USD", 2.00m, 1.82m), CancellationToken.None);

        var rows = await ReadAllAsync(job);
        rows.Count.ShouldBe(2);
        rows[0].PricingVersion.ShouldBe("price-a");
        rows[0].EstimatedCost.ShouldBe(0.61m);
        rows[1].PricingVersion.ShouldBe("price-b");
        rows[1].EstimatedCost.ShouldBe(2.00m);
    }

    [Fact]
    public async Task A_failed_run_keeps_the_usage_it_already_incurred()
    {
        var (job, queue) = await CreateClaimedJobAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);

        await store.RecordInvocationAsync(
            job, "extraction", Guid.NewGuid(), 1, Result(1_000, 500, "m", "d"), 2,
            new AiPricing("test-v1", "USD", 0.61m, 1.82m), CancellationToken.None);

        await queue.FailAsync(job, "invalid_model_output", "Parsing failed after the call.", retryable: false, CancellationToken.None);

        (await RecordCountAsync(job)).ShouldBe(1, "a failed run must not erase a charge the provider already booked");
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{job.RunId}'"))
            .ShouldBe("Failed");
    }

    [Fact]
    public async Task An_attempt_from_another_run_is_rejected_and_records_nothing()
    {
        var (job, _) = await CreateClaimedJobAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var foreign = job with { AttemptId = Guid.NewGuid() };

        await Should.ThrowAsync<InvalidOperationException>(() =>
            store.RecordInvocationAsync(
                foreign, "extraction", Guid.NewGuid(), 1, Result(1, 1, "m", "d"), 0,
                new AiPricing("test-v1", "USD", 0.61m, 1.82m), CancellationToken.None));

        (await RecordCountAsync(job)).ShouldBe(0);
    }

    [Fact]
    public async Task A_stage_re_executed_in_the_same_attempt_records_a_new_charge()
    {
        var (job, _) = await CreateClaimedJobAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var pricing = new AiPricing("test-v1", "USD", 0.61m, 1.82m);

        // Same attempt, same stage, same ordinal, different logical call: the second call was billed
        // and must not be mistaken for a replay of the first.
        await store.RecordInvocationAsync(job, "extraction", Guid.NewGuid(), 1, Result(1_000, 500, "m", "d"), 0, pricing, CancellationToken.None);
        await store.RecordInvocationAsync(job, "extraction", Guid.NewGuid(), 1, Result(1_000, 500, "m", "d"), 0, pricing, CancellationToken.None);

        (await RecordCountAsync(job)).ShouldBe(2);
        (await AggregateAsync(job.CompanyId)).InputTokens.ShouldBe(2_000);
    }

    [Fact]
    public async Task A_conflicting_replay_is_refused_and_the_counters_do_not_move()
    {
        var (job, _) = await CreateClaimedJobAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var pricing = new AiPricing("test-v1", "USD", 0.61m, 1.82m);
        var invocationId = Guid.NewGuid();

        await store.RecordInvocationAsync(job, "extraction", invocationId, 1, Result(1_000, 500, "m", "d"), 2, pricing, CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            store.RecordInvocationAsync(job, "extraction", invocationId, 1, Result(9_999, 500, "m", "d"), 2, pricing, CancellationToken.None));

        (await RecordCountAsync(job)).ShouldBe(1);
        (await AggregateAsync(job.CompanyId)).InputTokens.ShouldBe(1_000);
    }

    [Fact]
    public async Task Currency_is_enforced_by_the_database()
    {
        var (job, _) = await CreateClaimedJobAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);

        await store.RecordInvocationAsync(
            job, "extraction", Guid.NewGuid(), 1, Result(1_000, 500, "m", "d"), 0,
            new AiPricing("test-v1", "USD", 0.61m, 1.82m), CancellationToken.None);

        await Should.ThrowAsync<PostgresException>(() =>
            ExecuteAsync($"update platform.ai_usage_record set currency = 'EUR' where run_id = '{job.RunId}';"));
    }

    // ---------------------------------------------------------------- helpers

    private static AiCompletionResult Result(int? input, int? output, string model, string deployment)
    {
        using var document = JsonDocument.Parse("{}");
        return new AiCompletionResult(
            document.RootElement.Clone(), model, deployment, input, output, TimeSpan.Zero, "azure-foundry");
    }

    private sealed record UsageRow(
        Guid Company, Guid Document, Guid Attempt, string Stage, int Invocation, string Provider,
        string? Model, string? Deployment, long? InputTokens, long? OutputTokens, string UsageSource,
        decimal? EstimatedCost, string PricingVersion, int PagesInAggregate, long AggregateInput);

    private async Task<UsageRow> ReadSingleAsync(ProcessingJob job) => (await ReadAllAsync(job)).Single();

    private async Task<List<UsageRow>> ReadAllAsync(ProcessingJob job)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select u.company_id, u.document_id, u.attempt_id, u.stage, u.invocation_no, u.provider, u.model,
                   u.deployment, u.input_tokens, u.output_tokens, u.usage_source, u.estimated_cost_usd,
                   u.pricing_version,
                   coalesce((select pages from platform.processing_usage p
                             where p.company_id = u.company_id and p.period = date_trunc('month', now())::date), 0),
                   coalesce((select input_tokens from platform.processing_usage p
                             where p.company_id = u.company_id and p.period = date_trunc('month', now())::date), 0)
            from platform.ai_usage_record u
            where u.run_id = @run
            order by u.stage, u.invocation_no;
            """, connection);
        command.Parameters.AddWithValue("run", job.RunId);

        var rows = new List<UsageRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new UsageRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt32(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                reader.GetString(12),
                reader.GetInt32(13),
                reader.GetInt64(14)));
        }

        return rows;
    }

    private async Task<int> RecordCountAsync(ProcessingJob job) => Convert.ToInt32(
        await ScalarAsync($"select count(*) from platform.ai_usage_record where run_id = '{job.RunId}'"));

    private async Task<(long InputTokens, int Pages)> AggregateAsync(Guid company) =>
        (Convert.ToInt64(await ScalarAsync(
             $"select coalesce(sum(input_tokens),0) from platform.processing_usage where company_id = '{company}'")),
         Convert.ToInt32(await ScalarAsync(
             $"select coalesce(sum(pages),0) from platform.processing_usage where company_id = '{company}'")));

    private async Task<(ProcessingJob Job, ProcessingQueue Queue)> CreateClaimedJobAsync()
    {
        await ExecuteAsync("delete from platform.ai_usage_record;");
        await ExecuteAsync("delete from platform.processing_run;");

        var suffix = Guid.NewGuid().ToString("N");
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{companyId}', 'Usage test {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{userId}', 'usage-{suffix}@example.test', 'Usage Test');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{documentId}', 'usage/{suffix}.pdf', 'usage.pdf', 'application/pdf', 1024, '{userId}', '{companyId}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values ('{runId}', '{documentId}', '{companyId}');");

        _created.Add((companyId, userId, documentId));

        var queue = new ProcessingQueue(
            fixture.ConnectionString,
            new WorkerOptions { LeaseSeconds = 60, MaxAttempts = 3, BackoffSeconds = [0] });
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        return (job!, queue);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }
}
