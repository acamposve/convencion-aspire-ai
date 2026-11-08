using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Worker;
using Convenciones.Worker.Extraction;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ProcessingQueueTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<(Guid Company, Guid User, Guid Document)> _created = [];

    private static readonly WorkerOptions Options = new()
    {
        LeaseSeconds = 60,
        MaxAttempts = 3,
        BackoffSeconds = [0],
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (company, user, document) in _created)
        {
            await ExecuteAsync($"delete from platform.processing_run where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.document where id = '{document}';");
            await ExecuteAsync($"delete from platform.user_account where id = '{user}';");
            await ExecuteAsync($"delete from app.company where id = '{company}';");
        }
    }

    [Fact]
    public async Task Two_workers_never_claim_the_same_run()
    {
        var (runId, _) = await CreateRunAsync();
        var first = new ProcessingQueue(fixture.ConnectionString, Options);
        var second = new ProcessingQueue(fixture.ConnectionString, Options);

        var claims = await Task.WhenAll(
            first.ClaimAsync(CancellationToken.None),
            second.ClaimAsync(CancellationToken.None));

        claims.Count(claim => claim is not null).ShouldBe(1);
        claims.Single(claim => claim is not null)!.RunId.ShouldBe(runId);
    }

    [Fact]
    public async Task Transient_failure_requeues_and_exhausted_attempts_fail()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var job = await queue.ClaimAsync(CancellationToken.None);
            job.ShouldNotBeNull();
            job!.RunId.ShouldBe(runId);
            job.Attempt.ShouldBe(attempt);

            await queue.FailAsync(job, "provider_unavailable", "Temporary failure.", retryable: true, CancellationToken.None);
        }

        var status = await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'");
        status.ShouldBe("Failed");
        var attempts = await ScalarAsync($"select count(*) from platform.processing_attempt where run_id = '{runId}'");
        attempts.ShouldBe("3");
    }

    [Fact]
    public async Task Transient_failure_can_be_retried_and_completed()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);

        var first = await queue.ClaimAsync(CancellationToken.None);
        first.ShouldNotBeNull();
        await queue.FailAsync(first!, "provider_unavailable", "Temporary failure.", retryable: true, CancellationToken.None);

        var second = await queue.ClaimAsync(CancellationToken.None);
        second.ShouldNotBeNull();
        second!.Attempt.ShouldBe(2);
        await queue.CompleteAsync(second, CancellationToken.None);

        var status = await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'");
        status.ShouldBe("Completed");
    }

    [Fact]
    public async Task Lifecycle_writes_audit_entries()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);

        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await queue.FailAsync(job!, "provider_unavailable", "Temporary failure.", retryable: true, CancellationToken.None);
        var second = await queue.ClaimAsync(CancellationToken.None);
        second.ShouldNotBeNull();
        await queue.CompleteAsync(second!, CancellationToken.None);

        var actions = await ScalarAsync(
            $"select string_agg(action, ',' order by id) from platform.audit_entry where entity_id = '{runId}'");
        actions.ShouldContain("processing.started");
        actions.ShouldContain("processing.retried");
        actions.ShouldContain("processing.completed");
    }
    [Fact]
    public async Task Recovery_honours_company_and_run_scope()
    {
        var (runA, _) = await CreateRunAsync();
        var (runB, _) = await CreateRunCoreAsync(clear: false);
        var companyA = Guid.Parse(await ScalarAsync($"select company_id::text from platform.processing_run where id = '{runA}'"));

        await ExecuteAsync(
            $"update platform.processing_run set status = 'Running', attempts = 1, " +
            $"lease_expires_at = now() - interval '1 minute' where id in ('{runA}', '{runB}');");

        var queue = new ProcessingQueue(fixture.ConnectionString, new WorkerOptions
        {
            LeaseSeconds = 60,
            MaxAttempts = 3,
            BackoffSeconds = [0],
            RequireScope = true,
            AllowedCompanyId = companyA,
            AuthorizedCompanyId = companyA,
        });

        var recovered = await queue.RecoverAsync(CancellationToken.None);

        recovered.ShouldBe(1);
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runA}'"))
            .ShouldBe("Queued");
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runB}'"))
            .ShouldBe("Running");
    }

    [Fact]
    public async Task Expired_lease_is_recovered()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();

        await ExecuteAsync($"update platform.processing_run set lease_expires_at = now() - interval '1 minute' where id = '{runId}'");
        var recovered = await queue.RecoverAsync(CancellationToken.None);

        recovered.ShouldBe(1);
        var status = await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'");
        status.ShouldBe("Queued");
        var actions = await ScalarAsync(
            $"select string_agg(action, ',' order by id) from platform.audit_entry where entity_id = '{runId}'");
        actions.ShouldContain("processing.recovered");
    }

    [Fact]
    public async Task Claim_after_the_deadline_returns_nothing()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);

        var job = await queue.ClaimAsync(DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        job.ShouldBeNull();
        var status = await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'");
        status.ShouldBe("Queued");
    }
    [Fact]
    public async Task Cancelled_run_is_not_claimed()
    {
        var (runId, _) = await CreateRunAsync();
        await ExecuteAsync($"update platform.processing_run set status = 'Cancelled' where id = '{runId}'");
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);

        (await queue.ClaimAsync(CancellationToken.None)).ShouldBeNull();
        (await queue.IsCancelledAsync(runId, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Deleted_documents_are_not_claimed()
    {
        var (runId, documentId) = await CreateRunAsync();
        await ExecuteAsync(
            $"update platform.document set deleted_at = now() where id = '{documentId}';");

        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        (await queue.ClaimAsync(CancellationToken.None)).ShouldBeNull();
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Queued");
    }

    [Fact]
    public async Task Deleted_document_detected_after_claim_cancels_run_and_attempt()
    {
        var (runId, documentId) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();

        await ExecuteAsync($"update platform.document set deleted_at = now() where id = '{documentId}';");
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        await Should.ThrowAsync<DocumentDeletedException>(() =>
            store.LoadDocumentAsync(runId, CancellationToken.None));

        await queue.CancelAsync(
            job!,
            "document_deleted",
            "The document was deleted while the run was active.",
            CancellationToken.None);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Cancelled");
        (await ScalarAsync(
            $"select status::text || ':' || (finished_at is not null)::text " +
            $"from platform.processing_attempt where run_id = '{runId}'"))
            .ShouldBe("Cancelled:true");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{runId}' " +
            $"and action = 'processing.cancelled' and changes->>'code' = 'document_deleted'"))
            .ShouldBe("1");
        (await ScalarAsync($"select count(*) from platform.extracted_clause where run_id = '{runId}'"))
            .ShouldBe("0");
    }

    [Fact]
    public async Task Deleted_document_detected_after_load_aborts_persistence()
    {
        var (runId, documentId) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        (await store.LoadDocumentAsync(runId, CancellationToken.None)).ShouldNotBeNull();

        await ExecuteAsync($"update platform.document set deleted_at = now() where id = '{documentId}';");
        var merged = new MergedClause(1, null, null, "No debe persistirse", 1, 1, 0.9, false);
        await Should.ThrowAsync<DocumentDeletedException>(() =>
            store.SaveClausesAsync(job!, [merged], CancellationToken.None));

        await queue.CancelAsync(job!, "document_deleted", "The document was deleted.", CancellationToken.None);
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Cancelled");
        (await ScalarAsync($"select count(*) from platform.extracted_clause where run_id = '{runId}'"))
            .ShouldBe("0");
        (await ScalarAsync(
            $"select count(*) from platform.processing_attempt where run_id = '{runId}' " +
            $"and status = 'Running'")).ShouldBe("0");
    }

    [Fact]
    public async Task FailAsync_cannot_requeue_a_deleted_document()
    {
        var (runId, documentId) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await ExecuteAsync($"update platform.document set deleted_at = now() where id = '{documentId}';");

        await queue.FailAsync(
            job!,
            "unexpected_error",
            "A retryable-looking failure.",
            retryable: true,
            CancellationToken.None);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Cancelled");
        (await ScalarAsync(
            $"select status::text || ':' || (finished_at is not null)::text " +
            $"from platform.processing_attempt where run_id = '{runId}'"))
            .ShouldBe("Cancelled:true");
    }

    [Fact]
    public async Task CompleteAsync_cannot_complete_a_deleted_document()
    {
        var (runId, documentId) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await ExecuteAsync($"update platform.document set deleted_at = now() where id = '{documentId}';");

        await queue.CompleteAsync(job!, CancellationToken.None);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Cancelled");
        (await ScalarAsync(
            $"select status::text || ':' || (finished_at is not null)::text " +
            $"from platform.processing_attempt where run_id = '{runId}'"))
            .ShouldBe("Cancelled:true");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{runId}' " +
            $"and action = 'processing.cancelled' and changes->>'code' = 'document_deleted'"))
            .ShouldBe("1");
    }

    [Fact]
    public async Task Worker_catches_document_deleted_and_cancels_the_run()
    {
        var (runId, _) = await CreateRunAsync();
        var options = new WorkerOptions
        {
            Enabled = true,
            PollIntervalSeconds = 1,
            LeaseSeconds = 60,
            MaxAttempts = 3,
            BackoffSeconds = [0],
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            HealthFile = Path.Combine(Path.GetTempPath(), $"worker-{Guid.NewGuid():N}.ready"),
        };
        var queue = new ProcessingQueue(fixture.ConnectionString, options);
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new ProcessingWorker(
            queue,
            new DeletedDocumentStage(),
            options,
            new SignalLogger(signal));

        await worker.StartAsync(CancellationToken.None);
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Cancelled");
        (await ScalarAsync(
            $"select status::text || ':' || (finished_at is not null)::text " +
            $"from platform.processing_attempt where run_id = '{runId}'"))
            .ShouldBe("Cancelled:true");
    }

    private sealed class DeletedDocumentStage : IProcessingStage
    {
        public Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken) =>
            throw new DocumentDeletedException(job.DocumentId);
    }

    private sealed class SignalLogger(TaskCompletionSource signal) : ILogger<ProcessingWorker>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("stopped because document", StringComparison.Ordinal))
            {
                signal.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task Stale_worker_cannot_complete_or_fail_the_next_attempt()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var stale = await queue.ClaimAsync(CancellationToken.None);
        stale.ShouldNotBeNull();

        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' " +
            $"where id = '{runId}';");
        (await queue.RecoverAsync(CancellationToken.None)).ShouldBe(1);
        var current = await queue.ClaimAsync(CancellationToken.None);
        current.ShouldNotBeNull();
        current!.Attempt.ShouldBe(2);
        current.AttemptId.ShouldNotBe(stale!.AttemptId);

        await queue.CompleteAsync(stale, CancellationToken.None);
        await queue.FailAsync(stale, "stale_failure", "stale worker", retryable: false, CancellationToken.None);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Running");
        (await ScalarAsync(
            $"select status::text from platform.processing_attempt where run_id = '{runId}' and attempt_no = 2"))
            .ShouldBe("Running");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{runId}' " +
            $"and action in ('processing.completed', 'processing.failed')")).ShouldBe("0");

        await queue.CompleteAsync(current, CancellationToken.None);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Completed");
        (await ScalarAsync(
            $"select string_agg(status::text || ':' || (finished_at is not null)::text, E'\\n' order by attempt_no) " +
            $"from platform.processing_attempt where run_id = '{runId}'"))
            .ShouldBe("Failed:true\nCompleted:true");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{runId}' " +
            $"and action = 'processing.completed'")).ShouldBe("1");
    }

    [Fact]
    public async Task Stale_worker_cannot_finish_after_the_current_attempt_fails()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var stale = await queue.ClaimAsync(CancellationToken.None);
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' " +
            $"where id = '{runId}';");
        await queue.RecoverAsync(CancellationToken.None);
        var current = await queue.ClaimAsync(CancellationToken.None);
        current.ShouldNotBeNull();

        await queue.FailAsync(current!, "invalid_output", "permanent", retryable: false, CancellationToken.None);
        await queue.CompleteAsync(stale!, CancellationToken.None);
        await queue.FailAsync(stale!, "stale", "stale", retryable: false, CancellationToken.None);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Failed");
        (await ScalarAsync(
            $"select status::text from platform.processing_attempt where run_id = '{runId}' and attempt_no = 2"))
            .ShouldBe("Failed");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{runId}' " +
            $"and action = 'processing.completed'")).ShouldBe("0");
    }

    [Fact]
    public async Task Stale_worker_cannot_finish_after_cancellation()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var stale = await queue.ClaimAsync(CancellationToken.None);
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' " +
            $"where id = '{runId}';");
        await queue.RecoverAsync(CancellationToken.None);
        var current = await queue.ClaimAsync(CancellationToken.None);
        current.ShouldNotBeNull();

        await queue.CancelAsync(current!, CancellationToken.None);
        await queue.CompleteAsync(stale!, CancellationToken.None);
        await queue.FailAsync(stale!, "stale", "stale", retryable: false, CancellationToken.None);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Cancelled");
        (await ScalarAsync(
            $"select status::text from platform.processing_attempt where run_id = '{runId}' and attempt_no = 2"))
            .ShouldBe("Cancelled");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{runId}' " +
            $"and action in ('processing.completed', 'processing.failed')")).ShouldBe("0");
    }

    [Fact]
    public async Task Missing_or_repeated_attempt_finalization_is_a_noop()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();

        await queue.CompleteAsync(job! with { AttemptId = Guid.NewGuid() }, CancellationToken.None);
        await queue.FailAsync(job! with { AttemptId = Guid.NewGuid() }, "missing", "missing", retryable: false, CancellationToken.None);
        await queue.CompleteAsync(job!, CancellationToken.None);
        await queue.CompleteAsync(job!, CancellationToken.None);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Completed");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{runId}' " +
            $"and action = 'processing.completed'")).ShouldBe("1");
        (await ScalarAsync(
            $"select count(*) from platform.processing_attempt where run_id = '{runId}' " +
            $"and status = 'Completed'")).ShouldBe("1");
    }

    [Fact]
    public async Task Concurrent_completion_of_the_same_attempt_writes_one_terminal_result()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            await gate.Task;
            await queue.CompleteAsync(job!, CancellationToken.None);
        });
        var second = Task.Run(async () =>
        {
            await gate.Task;
            await queue.CompleteAsync(job!, CancellationToken.None);
        });
        gate.SetResult();
        await Task.WhenAll(first, second);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Completed");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{runId}' " +
            $"and action = 'processing.completed'")).ShouldBe("1");
    }

    [Fact]
    public async Task Stale_worker_cannot_write_clauses_or_classifications_after_recovery()
    {
        var (runId, documentId) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var stale = await queue.ClaimAsync(CancellationToken.None);
        stale.ShouldNotBeNull();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var categoryId = Guid.Parse(await ScalarAsync(
            "select id::text from platform.clause_category where key = 'pricing'"));

        var original = new MergedClause(1, null, null, "Resultado original", 1, 1, 0.9, false);
        await store.SaveClausesAsync(stale!, [original], CancellationToken.None);
        await store.SaveClassificationsAsync(
            stale!,
            [new ClassificationAssignment(
                Guid.Parse(await ScalarAsync(
                    $"select id::text from platform.extracted_clause where run_id = '{runId}'")),
                categoryId,
                0.9,
                true,
                "original")],
            0,
            CancellationToken.None);

        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' " +
            $"where id = '{runId}';");
        await queue.RecoverAsync(CancellationToken.None);
        var current = await queue.ClaimAsync(CancellationToken.None);
        current.ShouldNotBeNull();

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.SaveClausesAsync(
                stale!,
                [new MergedClause(1, null, null, "Resultado obsoleto", 1, 1, 0.9, false)],
                CancellationToken.None));
        var persistedClause = Guid.Parse(await ScalarAsync(
            $"select id::text from platform.extracted_clause where run_id = '{runId}'"));
        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.SaveClassificationsAsync(
                stale!,
                [new ClassificationAssignment(
                    persistedClause,
                    categoryId,
                    0.5,
                    true,
                    "obsoleto")],
                0,
                CancellationToken.None));

        (await ScalarAsync($"select text from platform.extracted_clause where run_id = '{runId}'"))
            .ShouldBe("Resultado original");
        (await ScalarAsync(
            $"select c.key from platform.clause_classification cc " +
            $"join platform.clause_category c on c.id = cc.category_id " +
            $"where cc.clause_id = (select id from platform.extracted_clause where run_id = '{runId}')"))
            .ShouldBe("pricing");
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Running");
        (await ScalarAsync(
            $"select status::text from platform.processing_attempt where run_id = '{runId}' and attempt_no = 2"))
            .ShouldBe("Running");
    }

    [Fact]
    public async Task Stale_worker_cannot_overwrite_completed_results_of_the_new_attempt()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var stale = await queue.ClaimAsync(CancellationToken.None);
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' " +
            $"where id = '{runId}';");
        await queue.RecoverAsync(CancellationToken.None);
        var current = await queue.ClaimAsync(CancellationToken.None);
        current.ShouldNotBeNull();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var categoryId = Guid.Parse(await ScalarAsync(
            "select id::text from platform.clause_category where key = 'scope'"));

        await store.SaveClausesAsync(
            current!,
            [new MergedClause(1, null, null, "Resultado de B", 1, 1, 0.9, false)],
            CancellationToken.None);
        await store.SaveClassificationsAsync(
            current!,
            [new ClassificationAssignment(
                Guid.Parse(await ScalarAsync(
                    $"select id::text from platform.extracted_clause where run_id = '{runId}'")),
                categoryId,
                0.8,
                true,
                "B")],
            0,
            CancellationToken.None);
        await queue.CompleteAsync(current!, CancellationToken.None);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.SaveClausesAsync(
                stale!,
                [new MergedClause(1, null, null, "A tardío", 1, 1, 0.9, false)],
                CancellationToken.None));
        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.SaveClassificationsAsync(
                stale!,
                [],
                0,
                CancellationToken.None));

        (await ScalarAsync($"select text from platform.extracted_clause where run_id = '{runId}'"))
            .ShouldBe("Resultado de B");
        (await ScalarAsync(
            $"select c.key from platform.clause_classification cc " +
            $"join platform.clause_category c on c.id = cc.category_id " +
            $"where cc.clause_id = (select id from platform.extracted_clause where run_id = '{runId}')"))
            .ShouldBe("scope");
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Completed");
    }

    [Fact]
    public async Task Invalid_or_foreign_attempt_ids_cannot_persist()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        var other = await CreateRunCoreAsync(clear: false);
        var otherJob = await new ProcessingQueue(fixture.ConnectionString, Options)
            .ClaimAsync(CancellationToken.None);
        otherJob.ShouldNotBeNull();
        otherJob!.RunId.ShouldBe(other.RunId);
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var clause = new MergedClause(1, null, null, "No debe entrar", 1, 1, 0.9, false);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.SaveClausesAsync(job! with { AttemptId = Guid.Empty }, [clause], CancellationToken.None));
        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.SaveClausesAsync(job! with { AttemptId = Guid.NewGuid() }, [clause], CancellationToken.None));
        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.SaveClausesAsync(
                job! with { AttemptId = otherJob.AttemptId, Attempt = otherJob.Attempt },
                [clause],
                CancellationToken.None));

        (await ScalarAsync($"select count(*) from platform.extracted_clause where run_id = '{runId}'"))
            .ShouldBe("0");
    }

    [Fact]
    public async Task Failed_persistence_rolls_back_clause_replacement()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        await store.SaveClausesAsync(
            job!,
            [new MergedClause(1, null, null, "Original", 1, 1, 0.9, false)],
            CancellationToken.None);

        await Should.ThrowAsync<Npgsql.PostgresException>(() =>
            store.SaveClausesAsync(
                job!,
                [
                    new MergedClause(1, null, null, "Duplicada A", 1, 1, 0.9, false),
                    new MergedClause(1, null, null, "Duplicada B", 1, 1, 0.9, false),
                ],
                CancellationToken.None));

        (await ScalarAsync($"select count(*) from platform.extracted_clause where run_id = '{runId}'"))
            .ShouldBe("1");
        (await ScalarAsync($"select text from platform.extracted_clause where run_id = '{runId}'"))
            .ShouldBe("Original");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Store_and_recovery_contend_on_the_run_row(bool storeFirst)
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' " +
            $"where id = '{runId}';");
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        Exception? storeError = null;

        await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "select id from platform.processing_run where id = @run for update;",
            blocker,
            blockerTransaction))
        {
            lockCommand.Parameters.AddWithValue("run", runId);
            await lockCommand.ExecuteScalarAsync();
        }

        Task Store() => Task.Run(async () =>
        {
            try
            {
                await store.SaveClausesAsync(
                    job!,
                    [new MergedClause(1, null, null, "Contención", 1, 1, 0.9, false)],
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                storeError = exception;
            }
        });
        Task Recovery() => Task.Run(() => queue.RecoverAsync(CancellationToken.None));

        Task first;
        Task second;
        if (storeFirst)
        {
            first = Store();
            await WaitForLockWaitersAsync(1);
            second = Recovery();
            await WaitForLockWaitersAsync(2);
        }
        else
        {
            first = Recovery();
            await WaitForLockWaitersAsync(1);
            second = Store();
            await WaitForLockWaitersAsync(2);
        }

        await blockerTransaction.CommitAsync();
        await Task.WhenAll(first, second);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Queued");
        (await ScalarAsync(
            $"select status::text from platform.processing_attempt where run_id = '{runId}' and attempt_no = 1"))
            .ShouldBe("Failed");
        (await ScalarAsync(
            $"select count(*) from platform.processing_attempt where run_id = '{runId}' and status = 'Running'"))
            .ShouldBe("0");
        if (storeFirst)
        {
            storeError.ShouldBeNull();
            (await ScalarAsync($"select text from platform.extracted_clause where run_id = '{runId}'"))
                .ShouldBe("Contención");
        }
        else
        {
            storeError.ShouldBeOfType<StaleProcessingAttemptException>();
            (await ScalarAsync($"select count(*) from platform.extracted_clause where run_id = '{runId}'"))
                .ShouldBe("0");
        }
    }

    private async Task WaitForLockWaitersAsync(int minimum)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var count = Convert.ToInt32(await ScalarAsync(
                "select count(*) from pg_stat_activity " +
                "where datname = current_database() and wait_event_type = 'Lock' " +
                "and pid <> pg_backend_pid();"));
            if (count >= minimum)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Expected at least {minimum} PostgreSQL lock waiter(s).");
    }

    [Fact]
    public async Task Recovery_cancels_an_expired_run_for_a_deleted_document()
    {
        var (runId, documentId) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' " +
            $"where id = '{runId}';" +
            $"update platform.document set deleted_at = now() where id = '{documentId}';");

        (await queue.RecoverAsync(CancellationToken.None)).ShouldBe(1);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Cancelled");
        (await ScalarAsync(
            $"select status::text || ':' || (finished_at is not null)::text " +
            $"from platform.processing_attempt where run_id = '{runId}'"))
            .ShouldBe("Cancelled:true");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{runId}' " +
            $"and action = 'processing.cancelled' and changes->>'code' = 'document_deleted'"))
            .ShouldBe("1");

        (await queue.RecoverAsync(CancellationToken.None)).ShouldBe(0);
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{runId}' " +
            $"and action = 'processing.cancelled'")).ShouldBe("1");
    }

    [Fact]
    public async Task Recovery_exhausted_attempts_closes_run_and_attempt()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        (await queue.ClaimAsync(CancellationToken.None)).ShouldNotBeNull();
        await ExecuteAsync(
            $"update platform.processing_run set attempts = 3, " +
            $"lease_expires_at = now() - interval '1 minute' where id = '{runId}';" +
            $"update platform.processing_attempt set attempt_no = 3 where run_id = '{runId}';");

        (await queue.RecoverAsync(CancellationToken.None)).ShouldBe(1);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Failed");
        (await ScalarAsync(
            $"select status::text || ':' || (finished_at is not null)::text " +
            $"from platform.processing_attempt where run_id = '{runId}' and attempt_no = 3"))
            .ShouldBe("Failed:true");
    }

    [Fact]
    public async Task Recovery_of_an_active_document_closes_the_interrupted_attempt()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        (await queue.ClaimAsync(CancellationToken.None)).ShouldNotBeNull();
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' " +
            $"where id = '{runId}';");

        (await queue.RecoverAsync(CancellationToken.None)).ShouldBe(1);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Queued");
        (await ScalarAsync(
            $"select status::text || ':' || (finished_at is not null)::text " +
            $"from platform.processing_attempt where run_id = '{runId}'"))
            .ShouldBe("Failed:true");
        (await queue.RecoverAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_cancel_and_recovery_are_serialized_by_fencing()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' " +
            $"where id = '{runId}';");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = Task.Run(async () =>
        {
            await gate.Task;
            return await queue.RecoverAsync(CancellationToken.None);
        });
        var cancellation = Task.Run(async () =>
        {
            await gate.Task;
            await queue.CancelAsync(job!, "cancelled", "Cancelled concurrently.", CancellationToken.None);
        });
        gate.SetResult();
        await Task.WhenAll(recovery, cancellation);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBeOneOf("Cancelled", "Queued");
        (await ScalarAsync(
            $"select count(*) from platform.processing_attempt where run_id = '{runId}' " +
            $"and status = 'Running'")).ShouldBe("0");
        (await ScalarAsync(
            $"select status::text from platform.processing_attempt where run_id = '{runId}'"))
            .ShouldBeOneOf("Failed", "Cancelled");
    }

    [Fact]
    public async Task Company_scope_never_claims_another_company()
    {
        var (runId, _) = await CreateRunAsync();
        var scoped = new ProcessingQueue(fixture.ConnectionString, new WorkerOptions
        {
            LeaseSeconds = 60,
            MaxAttempts = 3,
            BackoffSeconds = [0],
            AllowedCompanyId = Guid.NewGuid(),
        });

        (await scoped.ClaimAsync(CancellationToken.None)).ShouldBeNull();
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Queued");
    }

    [Fact]
    public async Task Allowed_document_scope_never_claims_another_document()
    {
        var (allowedRunId, allowedDocumentId) = await CreateRunAsync();
        var (otherRunId, _) = await CreateRunCoreAsync(clear: false);
        var scoped = new ProcessingQueue(fixture.ConnectionString, new WorkerOptions
        {
            LeaseSeconds = 60,
            MaxAttempts = 3,
            BackoffSeconds = [0],
            AllowedDocumentId = allowedDocumentId,
        });

        var job = await scoped.ClaimAsync(CancellationToken.None);

        job.ShouldNotBeNull();
        job!.RunId.ShouldBe(allowedRunId);
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{otherRunId}'"))
            .ShouldBe("Queued");
    }

    [Fact]
    public async Task Allowed_run_scope_never_claims_another_run()
    {
        var (allowedRunId, _) = await CreateRunAsync();
        var (otherRunId, _) = await CreateRunCoreAsync(clear: false);
        var scoped = new ProcessingQueue(fixture.ConnectionString, new WorkerOptions
        {
            LeaseSeconds = 60,
            MaxAttempts = 3,
            BackoffSeconds = [0],
            AllowedRunId = allowedRunId,
        });

        var job = await scoped.ClaimAsync(CancellationToken.None);

        job.ShouldNotBeNull();
        job!.RunId.ShouldBe(allowedRunId);
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{otherRunId}'"))
            .ShouldBe("Queued");
    }

    [Fact]
    public async Task Disallowed_scope_is_not_claimed()
    {
        var (runId, _) = await CreateRunAsync();
        var scoped = new ProcessingQueue(fixture.ConnectionString, new WorkerOptions
        {
            LeaseSeconds = 60,
            MaxAttempts = 3,
            BackoffSeconds = [0],
            AllowedDocumentId = Guid.NewGuid(),
        });

        (await scoped.ClaimAsync(CancellationToken.None)).ShouldBeNull();
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Queued");
    }

    private async Task<(Guid RunId, Guid DocumentId)> CreateRunAsync() =>
        await CreateRunCoreAsync(clear: true);

    private async Task<(Guid RunId, Guid DocumentId)> CreateRunCoreAsync(bool clear)
    {
        if (clear)
        {
            // This class owns the queue tables in the shared fixture; clear leftovers from prior tests.
            await ExecuteAsync("delete from platform.processing_run;");
        }

        var suffix = Guid.NewGuid().ToString("N");
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{companyId}', 'Queue test {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{userId}', 'queue-{suffix}@example.test', 'Queue Test');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{documentId}', 'queue/{suffix}.pdf', 'queue.pdf', 'application/pdf', 1024, '{userId}', '{companyId}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values ('{runId}', '{documentId}', '{companyId}');");

        _created.Add((companyId, userId, documentId));
        return (runId, documentId);
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
