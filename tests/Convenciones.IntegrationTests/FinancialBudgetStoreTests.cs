using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Ai;
using Convenciones.Worker;
using Convenciones.Worker.Financial;
using Convenciones.Worker.Extraction;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-005 PR B: budget reservation accounting on PostgreSQL. No provider is involved; the estimate is
/// supplied by the caller, exactly as the settlement wiring will supply it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FinancialBudgetStoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<(Guid Company, Guid User, Guid Document)> _created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (company, user, document) in _created)
        {
            await ExecuteAsync($"delete from platform.ai_budget_reservation where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.ai_budget_shadow where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.ai_budget_audit where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.ai_budget where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.ai_usage_record where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.processing_run where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.processing_usage where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.document where id = '{document}';");
            await ExecuteAsync($"delete from platform.user_account where id = '{user}';");
            await ExecuteAsync($"delete from app.company where id = '{company}';");
        }
    }

    [Fact]
    public async Task No_budget_row_runs_in_shadow_without_touching_counters()
    {
        var job = await CreateJobAsync();
        var store = new FinancialBudgetStore(fixture.ConnectionString);

        var decision = await store.ReserveAsync(job, "extraction", Guid.NewGuid(), 10m, CancellationToken.None);

        decision.Mode.ShouldBe(ReservationMode.Shadow);
        decision.Admitted.ShouldBeTrue();
        (await CountAsync("ai_budget_shadow", job.CompanyId)).ShouldBe(1);
        (await CountAsync("ai_budget_reservation", job.CompanyId)).ShouldBe(0);
    }

    [Fact]
    public async Task Shadow_budget_records_the_estimate_and_moves_nothing()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "shadow");
        var store = new FinancialBudgetStore(fixture.ConnectionString);

        var decision = await store.ReserveAsync(job, "extraction", Guid.NewGuid(), 10m, CancellationToken.None);

        decision.Mode.ShouldBe(ReservationMode.Shadow);
        var counters = await BudgetAsync(budgetId);
        counters.Reserved.ShouldBe(0m);
        counters.Spent.ShouldBe(0m);
        (await CountAsync("ai_budget_shadow", job.CompanyId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_enforced_budget_reserves_the_estimate()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        var decision = await store.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);

        decision.Mode.ShouldBe(ReservationMode.Enforced);
        decision.Admitted.ShouldBeTrue();
        (await BudgetAsync(budgetId)).Reserved.ShouldBe(10m);
        (await ReservationStatusAsync(invocationId)).ShouldBe("reserved");
    }

    [Fact]
    public async Task Reserving_the_same_invocation_twice_moves_the_counter_once()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await store.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await store.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);

        (await BudgetAsync(budgetId)).Reserved.ShouldBe(10m);
        (await CountAsync("ai_budget_reservation", job.CompanyId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_reservation_leaves_no_partial_state()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 5m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);

        var decision = await store.ReserveAsync(job, "extraction", Guid.NewGuid(), 10m, CancellationToken.None);

        decision.Admitted.ShouldBeFalse();
        var counters = await BudgetAsync(budgetId);
        counters.Reserved.ShouldBe(0m);
        (await CountAsync("ai_budget_reservation", job.CompanyId)).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_reservations_cannot_exceed_the_budget()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);

        var first = store.ReserveAsync(job, "extraction", Guid.NewGuid(), 60m, CancellationToken.None);
        var second = store.ReserveAsync(job, "extraction", Guid.NewGuid(), 60m, CancellationToken.None);

        var decisions = await Task.WhenAll(first, second);

        decisions.Count(decision => decision.Admitted).ShouldBe(1);
        var counters = await BudgetAsync(budgetId);
        counters.Reserved.ShouldBe(60m);
        (await CountAsync("ai_budget_reservation", job.CompanyId)).ShouldBe(1);
    }

    [Fact]
    public async Task Settlement_above_the_estimate_spends_the_actual_cost()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await store.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await store.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await store.SettleAsync(job.CompanyId, invocationId, 12m, CancellationToken.None);

        var counters = await BudgetAsync(budgetId);
        counters.Reserved.ShouldBe(0m);
        counters.Spent.ShouldBe(12m);
        (await ReservationStatusAsync(invocationId)).ShouldBe("settled");
    }

    [Fact]
    public async Task Settlement_below_the_estimate_releases_the_difference()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await store.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await store.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await store.SettleAsync(job.CompanyId, invocationId, 7m, CancellationToken.None);

        var counters = await BudgetAsync(budgetId);
        counters.Reserved.ShouldBe(0m);
        counters.Spent.ShouldBe(7m);
    }

    [Fact]
    public async Task Unknown_usage_is_settled_at_the_estimate_and_reconciled_by_delta()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await store.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await store.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await store.SettleAsync(job.CompanyId, invocationId, null, CancellationToken.None);

        (await ReservationStatusAsync(invocationId)).ShouldBe("settled_estimated");
        (await BudgetAsync(budgetId)).Spent.ShouldBe(10m);

        await store.ReconcileAsync(job, invocationId, 12m, CancellationToken.None);

        (await BudgetAsync(budgetId)).Spent.ShouldBe(12m, "the delta is applied, not the full actual cost");
        (await ReservationStatusAsync(invocationId)).ShouldBe("reconciled");
    }

    [Fact]
    public async Task An_identical_reconciliation_is_a_no_op()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await store.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await store.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await store.SettleAsync(job.CompanyId, invocationId, null, CancellationToken.None);
        await store.ReconcileAsync(job, invocationId, 12m, CancellationToken.None);
        await store.ReconcileAsync(job, invocationId, 12m, CancellationToken.None);

        (await BudgetAsync(budgetId)).Spent.ShouldBe(12m);
    }

    [Fact]
    public async Task A_conflicting_reconciliation_raises_and_changes_nothing()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await store.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await store.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await store.SettleAsync(job.CompanyId, invocationId, null, CancellationToken.None);
        await store.ReconcileAsync(job, invocationId, 12m, CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            store.ReconcileAsync(job, invocationId, 14m, CancellationToken.None));

        (await BudgetAsync(budgetId)).Spent.ShouldBe(12m);
        (await ReservationStatusAsync(invocationId)).ShouldBe("reconciled");
    }

    [Fact]
    public async Task A_submitted_invocation_cannot_be_released()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await store.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await store.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await store.ReleaseAsync(job.CompanyId, invocationId, CancellationToken.None);

        (await ReservationStatusAsync(invocationId)).ShouldBe("submitted");
        (await BudgetAsync(budgetId)).Reserved.ShouldBe(10m);
    }

    [Fact]
    public async Task A_reservation_that_was_never_submitted_is_released()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var store = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await store.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await store.ReleaseAsync(job.CompanyId, invocationId, CancellationToken.None);

        (await ReservationStatusAsync(invocationId)).ShouldBe("released");
        (await BudgetAsync(budgetId)).Reserved.ShouldBe(0m);
    }

    [Fact]
    public async Task Usage_recording_settles_the_reservation_in_the_same_transaction()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var pricing = new AiPricing("test-v1", "USD", 0.61m, 1.82m);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await store.RecordInvocationAsync(
            job, "extraction", invocationId, 1, Result(1_000_000, 0, "m", "d"), pages: 2, pricing,
            CancellationToken.None);

        var counters = await BudgetAsync(budgetId);
        counters.Reserved.ShouldBe(0m);
        counters.Spent.ShouldBe(0.61m);
        (await ReservationStatusAsync(invocationId)).ShouldBe("settled");

        var period = await ScalarAsync(
            $"select period::text from platform.ai_usage_record where invocation_id = '{invocationId}'");
        period.ShouldNotBeNullOrWhiteSpace("the ledger row carries the reservation period");
    }

    [Fact]
    public async Task Unreported_tokens_settle_at_the_reservation_estimate()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var store = new NpgsqlClauseStore(fixture.ConnectionString);

        var invocationId = Guid.NewGuid();
        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await store.RecordInvocationAsync(
            job, "extraction", invocationId, 1, Result(null, null, "m", "d"), pages: 0,
            new AiPricing("test-v1", "USD", 0.61m, 1.82m), CancellationToken.None);

        (await ReservationStatusAsync(invocationId)).ShouldBe("settled_estimated");
        var counters = await BudgetAsync(budgetId);
        counters.Reserved.ShouldBe(0m);
        counters.Spent.ShouldBe(10m, "unknown usage is charged at the estimate, never at zero");
    }

    [Fact]
    public async Task Activation_converts_submitted_shadow_invocations_and_releases_the_rest()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "shadow");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var submitted = Guid.NewGuid();
        var neverSubmitted = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", submitted, 10m, CancellationToken.None);
        await budget.ReserveAsync(job, "extraction", neverSubmitted, 4m, CancellationToken.None);
        await budget.MarkSubmittedAsync(job, submitted, CancellationToken.None);

        var result = await budget.ActivateAsync(
            job.CompanyId, CurrentPeriod(), "operator@test", "first enforced month", null, CancellationToken.None);

        result.Converted.ShouldBe(1);
        result.Released.ShouldBe(1);
        result.Spent.ShouldBe(10m);

        var counters = await BudgetAsync(budgetId);
        counters.Spent.ShouldBe(10m);
        (await ScalarAsync($"select enforcement from platform.ai_budget where id = '{budgetId}'"))
            .ShouldBe("enforced");
        (await ScalarAsync($"select status from platform.ai_budget_reservation where invocation_id = '{submitted}'"))
            .ShouldBe("settled_estimated");
        (await ScalarAsync($"select origin from platform.ai_budget_reservation where invocation_id = '{submitted}'"))
            .ShouldBe("shadow_cutover");
        (await ScalarAsync($"select status from platform.ai_budget_shadow where invocation_id = '{submitted}'"))
            .ShouldBe("converted");
        (await ScalarAsync($"select status from platform.ai_budget_shadow where invocation_id = '{neverSubmitted}'"))
            .ShouldBe("released");
        (await ScalarAsync($"select count(*) from platform.ai_budget_audit where company_id = '{job.CompanyId}'"))
            .ShouldBe("1");
    }

    [Fact]
    public async Task Activation_after_conversion_reconciles_the_liability_by_delta()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "shadow");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await budget.ActivateAsync(job.CompanyId, CurrentPeriod(), "ops", "cutover", null, CancellationToken.None);

        await budget.ReconcileAsync(job, invocationId, 12m, CancellationToken.None);

        (await BudgetAsync(budgetId)).Spent.ShouldBe(12m, "the recognised estimate is corrected, not doubled");
    }

    [Fact]
    public async Task Activation_fails_closed_when_unknown_usage_is_unresolved()
    {
        var job = await CreateJobAsync();
        await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "shadow");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await store.RecordInvocationAsync(
            job, "extraction", invocationId, 1, Result(null, null, "m", "d"), 0,
            new AiPricing("test-v1", "USD", 0.61m, 1.82m), CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(() => budget.ActivateAsync(
            job.CompanyId, CurrentPeriod(), "ops", "cutover", null, CancellationToken.None));

        (await ScalarAsync($"select enforcement from platform.ai_budget where company_id = '{job.CompanyId}'"))
            .ShouldBe("shadow", "a refused activation changes nothing");
    }

    [Fact]
    public async Task Activation_without_a_budget_row_is_refused()
    {
        var job = await CreateJobAsync();
        var budget = new FinancialBudgetStore(fixture.ConnectionString);

        await Should.ThrowAsync<InvalidOperationException>(() => budget.ActivateAsync(
            job.CompanyId, CurrentPeriod(), "ops", "cutover", null, CancellationToken.None));
    }

    [Fact]
    public async Task Activation_is_idempotent()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);

        var result = await budget.ActivateAsync(
            job.CompanyId, CurrentPeriod(), "ops", "cutover", null, CancellationToken.None);

        result.Converted.ShouldBe(0);
        (await BudgetAsync(budgetId)).Spent.ShouldBe(0m);
        (await ScalarAsync($"select count(*) from platform.ai_budget_audit where company_id = '{job.CompanyId}'"))
            .ShouldBe("0", "no audit row is written for a no-op activation");
    }

    [Fact]
    public async Task Sweep_settles_an_expired_submitted_reservation_at_its_estimate()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await ExecuteAsync(
            $"update platform.ai_budget_reservation set expires_at = now() - interval '1 hour' where invocation_id = '{invocationId}';");

        var settled = await budget.SweepAsync(CancellationToken.None);

        settled.ShouldBeGreaterThanOrEqualTo(1);
        (await ReservationStatusAsync(invocationId)).ShouldBe("settled_estimated");
        var counters = await BudgetAsync(budgetId);
        counters.Reserved.ShouldBe(0m);
        counters.Spent.ShouldBe(10m);
    }

    [Fact]
    public async Task Financial_history_blocks_deleting_the_run()
    {
        var job = await CreateJobAsync();
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await store.RecordInvocationAsync(
            job, "extraction", invocationId, 1, Result(1_000, 500, "m", "d"), 0,
            new AiPricing("test-v1", "USD", 0.61m, 1.82m), CancellationToken.None);

        await Should.ThrowAsync<PostgresException>(() =>
            ExecuteAsync($"delete from platform.processing_run where id = '{job.RunId}';"));
    }

    [Fact]
    public async Task A_late_response_reconciles_an_estimated_settlement_by_delta()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await budget.SettleAsync(job.CompanyId, invocationId, null, CancellationToken.None);

        (await BudgetAsync(budgetId)).Spent.ShouldBe(10m);

        // The provider answered after the sweeper had already recognised the estimate.
        await store.RecordInvocationAsync(
            job, "extraction", invocationId, 1, Result(1_000_000, 0, "m", "d"), pages: 0,
            new AiPricing("test-v1", "USD", 12m, 0m), CancellationToken.None);

        (await BudgetAsync(budgetId)).Spent.ShouldBe(12m, "the estimate is corrected, not left behind");
        (await ReservationStatusAsync(invocationId)).ShouldBe("reconciled");
    }

    [Fact]
    public async Task A_shadow_invocation_released_by_activation_cannot_be_submitted()
    {
        var job = await CreateJobAsync();
        await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "shadow");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await budget.ActivateAsync(job.CompanyId, CurrentPeriod(), "ops", "cutover", null, CancellationToken.None);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None));

        (await ScalarAsync(
            $"select coalesce(submitted_at::text, 'none') from platform.ai_budget_shadow where invocation_id = '{invocationId}'"))
            .ShouldBe("none", "a released invocation must not be marked as submitted");
    }

    [Fact]
    public async Task Repeated_submission_by_the_same_attempt_is_idempotent()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);

        (await ReservationStatusAsync(invocationId)).ShouldBe("submitted");
        (await BudgetAsync(budgetId)).Reserved.ShouldBe(10m, "the reservation is held once");
    }

    [Fact]
    public async Task Submission_from_a_different_attempt_is_rejected()
    {
        var job = await CreateJobAsync();
        await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();
        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            budget.MarkSubmittedAsync(job with { AttemptId = Guid.NewGuid() }, invocationId, CancellationToken.None));

        (await ReservationStatusAsync(invocationId)).ShouldBe("reserved");
    }

    [Fact]
    public async Task Submission_of_a_released_reservation_is_rejected()
    {
        var job = await CreateJobAsync();
        await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await budget.ReleaseAsync(job.CompanyId, invocationId, CancellationToken.None);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None));

        (await ReservationStatusAsync(invocationId)).ShouldBe("released");
    }

    [Fact]
    public async Task Submission_of_a_settled_reservation_is_rejected()
    {
        var job = await CreateJobAsync();
        await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await budget.SettleAsync(job.CompanyId, invocationId, 12m, CancellationToken.None);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None));

        (await ReservationStatusAsync(invocationId)).ShouldBe("settled");
    }

    [Fact]
    public async Task Concurrent_repeated_submission_is_idempotent()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "enforced");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);

        var first = budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        var second = budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await Task.WhenAll(first, second);

        (await ReservationStatusAsync(invocationId)).ShouldBe("submitted");
        (await BudgetAsync(budgetId)).Reserved.ShouldBe(10m);
    }

    [Fact]
    public async Task Shadow_submission_blocks_on_the_financial_lock()
    {
        var job = await CreateJobAsync();
        await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "shadow");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();
        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);

        await using var holder = new NpgsqlConnection(fixture.ConnectionString);
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(platform.fin_lock_key(@company, @period));", holder, holderTransaction))
        {
            command.Parameters.AddWithValue("company", job.CompanyId);
            command.Parameters.Add(new NpgsqlParameter("period", NpgsqlTypes.NpgsqlDbType.Date)
            {
                Value = CurrentPeriod(),
            });
            await command.ExecuteNonQueryAsync();
        }

        var submission = budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        await WaitForAdvisoryWaiterAsync();
        await holderTransaction.CommitAsync();
        await submission;

        (await ScalarAsync(
            $"select coalesce(submitted_at::text, 'none') from platform.ai_budget_shadow where invocation_id = '{invocationId}'"))
            .ShouldNotBe("none", "the marker was written after the lock was released");
    }

    [Fact]
    public async Task Activation_before_submission_releases_and_cannot_be_submitted()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "shadow");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        var activation = await budget.ActivateAsync(
            job.CompanyId, CurrentPeriod(), "ops", "cutover", null, CancellationToken.None);

        activation.Converted.ShouldBe(0);
        activation.Released.ShouldBe(1);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None));

        (await BudgetAsync(budgetId)).Spent.ShouldBe(0m);
        (await ScalarAsync($"select status from platform.ai_budget_shadow where invocation_id = '{invocationId}'"))
            .ShouldBe("released");
    }

    [Fact]
    public async Task Submission_before_activation_is_converted_into_a_liability()
    {
        var job = await CreateJobAsync();
        var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "shadow");
        var budget = new FinancialBudgetStore(fixture.ConnectionString);
        var invocationId = Guid.NewGuid();

        await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);
        await budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
        var activation = await budget.ActivateAsync(
            job.CompanyId, CurrentPeriod(), "ops", "cutover", null, CancellationToken.None);

        activation.Converted.ShouldBe(1);
        (await BudgetAsync(budgetId)).Spent.ShouldBe(10m);
        (await ScalarAsync($"select status from platform.ai_budget_reservation where invocation_id = '{invocationId}'"))
            .ShouldBe("settled_estimated");
        (await ScalarAsync($"select status from platform.ai_budget_shadow where invocation_id = '{invocationId}'"))
            .ShouldBe("converted");
    }

    [Fact]
    public async Task Concurrent_activation_and_submission_have_exactly_one_valid_outcome()
    {
        var budget = new FinancialBudgetStore(fixture.ConnectionString);

        for (var iteration = 0; iteration < 10; iteration++)
        {
            var job = await CreateJobAsync();
            var budgetId = await CreateBudgetAsync(job.CompanyId, limit: 100m, enforcement: "shadow");
            var invocationId = Guid.NewGuid();
            await budget.ReserveAsync(job, "extraction", invocationId, 10m, CancellationToken.None);

            var submission = budget.MarkSubmittedAsync(job, invocationId, CancellationToken.None);
            var activation = budget.ActivateAsync(
                job.CompanyId, CurrentPeriod(), "ops", "cutover", null, CancellationToken.None);

            try
            {
                await Task.WhenAll(submission, activation);
            }
            catch (StaleProcessingAttemptException)
            {
                // Activation won the lock and released the invocation; the submission was refused.
            }

            var shadowStatus = await ScalarAsync(
                $"select status from platform.ai_budget_shadow where invocation_id = '{invocationId}'");
            var spent = await ScalarDecimalAsync($"select spent_amount from platform.ai_budget where id = '{budgetId}'");

            if (shadowStatus == "converted")
            {
                spent.ShouldBe(10m, "the converted liability is recognised once");
                (await ScalarAsync(
                    $"select status from platform.ai_budget_reservation where invocation_id = '{invocationId}'"))
                    .ShouldBe("settled_estimated");
            }
            else
            {
                shadowStatus.ShouldBe("released");
                spent.ShouldBe(0m, "a released invocation is not a liability");
                (await ScalarAsync(
                    $"select count(*) from platform.ai_budget_reservation where invocation_id = '{invocationId}'"))
                    .ShouldBe("0");
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static DateOnly CurrentPeriod()
    {
        var now = DateTime.UtcNow;
        return new DateOnly(now.Year, now.Month, 1);
    }

    private async Task WaitForAdvisoryWaiterAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "select count(*) from pg_locks where locktype = 'advisory' and not granted;", connection);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The submission never blocked on the financial advisory lock.");
    }

    private static AiCompletionResult Result(int? input, int? output, string model, string deployment)
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        return new AiCompletionResult(
            document.RootElement.Clone(), model, deployment, input, output, TimeSpan.Zero, "azure-foundry");
    }

    private async Task<ProcessingJob> CreateJobAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{companyId}', 'Budget test {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{userId}', 'budget-{suffix}@example.test', 'Budget Test');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{documentId}', 'budget/{suffix}.pdf', 'budget.pdf', 'application/pdf', 1024, '{userId}', '{companyId}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values ('{runId}', '{documentId}', '{companyId}');" +
            $"insert into platform.processing_attempt (id, run_id, company_id, attempt_no) values ('{attemptId}', '{runId}', '{companyId}', 1);" +
            $"update platform.processing_run set status = 'Running' where id = '{runId}';");

        _created.Add((companyId, userId, documentId));
        return new ProcessingJob(runId, documentId, companyId, 1, attemptId);
    }

    private async Task<Guid> CreateBudgetAsync(Guid companyId, decimal limit, string enforcement)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            $"insert into platform.ai_budget (id, company_id, period_start, period_end, limit_amount, enforcement) " +
            $"values ('{id}', '{companyId}', (date_trunc('month', now()))::date, " +
            $"(date_trunc('month', now()) + interval '1 month - 1 day')::date, {limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}, '{enforcement}');");
        return id;
    }

    private async Task<(decimal Reserved, decimal Spent)> BudgetAsync(Guid budgetId) =>
        (await ScalarDecimalAsync($"select reserved_amount from platform.ai_budget where id = '{budgetId}'"),
         await ScalarDecimalAsync($"select spent_amount from platform.ai_budget where id = '{budgetId}'"));

    private async Task<string> ReservationStatusAsync(Guid invocationId) =>
        await ScalarAsync($"select status from platform.ai_budget_reservation where invocation_id = '{invocationId}'");

    private async Task<int> CountAsync(string table, Guid companyId) =>
        Convert.ToInt32(await ScalarAsync($"select count(*) from platform.{table} where company_id = '{companyId}'"));

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

    private async Task<decimal> ScalarDecimalAsync(string sql) => Convert.ToDecimal(await ScalarAsync(sql));
}
