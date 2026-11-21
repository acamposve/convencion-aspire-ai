using System;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Worker;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P2-004: a run transitions only when its attempt closes exactly once, and a stale call stays a no-op.
/// </summary>
/// <remarks>
/// The zero-row case is produced with a temporary trigger that drops the attempt update while the run
/// update succeeds — the same ordering an interleaving bug would create. The test then proves the whole
/// transaction is rolled back, including the audit entry.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AttemptInvariantTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Guid _company;
    private Guid _user;
    private Guid _document;
    private Guid _run;

    public async Task InitializeAsync()
    {
        _company = Guid.NewGuid();
        _user = Guid.NewGuid();
        _document = Guid.NewGuid();
        _run = Guid.NewGuid();
        var suffix = _company.ToString("N")[..8];

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{_company}', 'Attempt invariant {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{_user}', 'p2004-{suffix}@example.test', 'Invariant');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{_document}', 'p2004/{suffix}.pdf', 'invariant.pdf', 'application/pdf', 1, '{_user}', '{_company}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values ('{_run}', '{_document}', '{_company}');");
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync("drop trigger if exists p2004_block_close on platform.processing_attempt;");

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "set session_replication_role = replica;");
        await ExecuteAsync(connection,
            $"delete from platform.audit_entry where entity_id = '{_run}';" +
            $"delete from platform.processing_run where id = '{_run}';" +
            $"delete from platform.document where id = '{_document}';" +
            $"delete from platform.user_account where id = '{_user}';" +
            $"delete from app.company where id = '{_company}';");
        await ExecuteAsync(connection, "set session_replication_role = origin;");
    }

    [Fact]
    public async Task A_run_transition_without_the_attempt_close_rolls_everything_back()
    {
        var (queue, job) = await ClaimAsync();
        await BlockAttemptCloseAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => queue.CompleteAsync(job, CancellationToken.None));

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{_run}';"))
            .ShouldBe("Running", "the run update is rolled back with the failed attempt close");
        (await ScalarAsync(
            $"select status::text from platform.processing_attempt where run_id = '{_run}' and attempt_no = 1;"))
            .ShouldBe("Running");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{_run}' and action = 'processing.completed';"))
            .ShouldBe("0", "the audit entry is rolled back too");
    }

    [Fact]
    public async Task A_failed_attempt_close_after_a_fail_transition_also_rolls_back()
    {
        var (queue, job) = await ClaimAsync();
        await BlockAttemptCloseAsync();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            queue.FailAsync(job, "boom", "stage failed", retryable: false, CancellationToken.None));

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{_run}';"))
            .ShouldBe("Running");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{_run}' and action = 'processing.failed';"))
            .ShouldBe("0");
    }

    [Fact]
    public async Task Completing_twice_is_a_stale_no_op()
    {
        var (queue, job) = await ClaimAsync();

        await queue.CompleteAsync(job, CancellationToken.None);
        await queue.CompleteAsync(job, CancellationToken.None);

        (await ScalarAsync($"select status::text from platform.processing_run where id = '{_run}';"))
            .ShouldBe("Completed");
        (await ScalarAsync(
            $"select count(*) from platform.processing_attempt where run_id = '{_run}' and status = 'Completed';"))
            .ShouldBe("1", "the attempt closes exactly once");
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(ProcessingQueue Queue, ProcessingJob Job)> ClaimAsync()
    {
        var queue = new ProcessingQueue(
            fixture.ConnectionString,
            new WorkerOptions { LeaseSeconds = 60, MaxAttempts = 3, BackoffSeconds = [0] });

        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        return (queue, job!);
    }

    private Task BlockAttemptCloseAsync() => ExecuteAsync(
        """
        create or replace function p2004_block_attempt_close() returns trigger
        language plpgsql as $$
        begin
            if old.status = 'Running'::platform.processing_status then
                return null;
            end if;
            return new;
        end $$;
        """ +
        "drop trigger if exists p2004_block_close on platform.processing_attempt;" +
        "create trigger p2004_block_close before update on platform.processing_attempt " +
        "for each row execute function p2004_block_attempt_close();");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
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
