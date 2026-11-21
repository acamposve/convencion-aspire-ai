using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Api.Endpoints;
using Convenciones.Worker;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-010: the claim/delete race. Deletion soft-deletes the document and cancels its active runs in one
/// transaction; claiming locks the run row before it becomes Running. These tests force both orders and
/// the concurrent interleaving, then assert the persisted invariant: a deleted document never has a
/// Queued or Running run, and no attempt outlives its run.
/// </summary>
[Collection(DocumentsCollection.Name)]
public sealed class ClaimDeleteRaceTests(DocumentsFixture fixture) : IAsyncLifetime
{
    private static readonly Guid EditorA = new("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid EditorB = new("b1000000-0000-0000-0000-000000000002");
    private static readonly Guid CompanyA = new("b2000000-0000-0000-0000-000000000001");
    private static readonly Guid CompanyB = new("b2000000-0000-0000-0000-000000000002");

    private readonly List<Guid> _documents = [];

    public async Task DisposeAsync()
    {
        foreach (var document in _documents)
        {
            await ExecuteAsync($"delete from platform.processing_attempt where run_id in (select id from platform.processing_run where document_id = '{document}');");
            await ExecuteAsync($"delete from platform.processing_run where document_id = '{document}';");
            await ExecuteAsync($"delete from platform.document where id = '{document}';");
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Claim_after_a_committed_delete_returns_nothing()
    {
        var document = await CreateDocumentAndRunAsync(CompanyA);
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var version = await RowVersionAsync(client, document);
        (await DeleteAsync(client, document, version)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var queue = NewQueue();
        (await queue.ClaimAsync(CancellationToken.None)).ShouldBeNull(
            "a run whose document was deleted must never be claimed");

        (await ActiveRunCountAsync(document)).ShouldBe(0);
    }

    [Fact]
    public async Task Claim_before_delete_leaves_no_active_run_or_running_attempt()
    {
        var document = await CreateDocumentAndRunAsync(CompanyA);
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var queue = NewQueue();

        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        job!.Attempt.ShouldBe(1);

        var version = await RowVersionAsync(client, document);
        (await DeleteAsync(client, document, version)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ActiveRunCountAsync(document)).ShouldBe(0);
        (await RunningAttemptCountAsync(job.RunId)).ShouldBe(0);
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{job.RunId}'"))
            .ShouldBe("Cancelled");
        (await ScalarAsync($"select status::text from platform.processing_attempt where run_id = '{job.RunId}'"))
            .ShouldBe("Cancelled");
        (await ScalarAsync($"select deleted_at is not null from platform.document where id = '{document}'"))
            .ShouldBe("True");
    }

    [Fact]
    public async Task Concurrent_claim_and_delete_never_leave_an_active_run()
    {
        // Barrier-synchronised, repeated: the invariant must hold whichever side wins the row lock.
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var document = await CreateDocumentAndRunAsync(CompanyA);
            using var host = new ApiFactory(fixture.ConnectionString);
            using var client = ClientFor(host, EditorA);
            var queue = NewQueue();
            var version = await RowVersionAsync(client, document);

            using var barrier = new Barrier(2);
            var claim = Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                return await queue.ClaimAsync(CancellationToken.None);
            });
            var delete = Task.Run(async () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                return await DeleteAsync(client, document, version);
            });

            var job = await claim;
            var response = await delete;

            response.StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                $"iteration {iteration}: {await response.Content.ReadAsStringAsync()}");

            // Whichever order won, the committed state must be coherent.
            (await ScalarAsync($"select deleted_at is not null from platform.document where id = '{document}'"))
                .ShouldBe("True");
            (await ActiveRunCountAsync(document)).ShouldBe(0);
            if (job is not null)
            {
                (await ScalarAsync($"select status::text from platform.processing_run where id = '{job.RunId}'"))
                    .ShouldBe("Cancelled");
                (await RunningAttemptCountAsync(job.RunId)).ShouldBe(0);
            }
        }
    }

    [Fact]
    public async Task Delete_still_cancels_a_run_that_was_recovered_after_its_lease_expired()
    {
        var document = await CreateDocumentAndRunAsync(CompanyA);
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var queue = NewQueue();

        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' where id = '{job!.RunId}';");
        (await queue.RecoverAsync(CancellationToken.None)).ShouldBe(1);
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{job.RunId}'"))
            .ShouldBe("Queued");

        var version = await RowVersionAsync(client, document);
        (await DeleteAsync(client, document, version)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ActiveRunCountAsync(document)).ShouldBe(0);
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{job.RunId}'"))
            .ShouldBe("Cancelled");
    }

    [Fact]
    public async Task Deleting_company_A_document_does_not_touch_company_B_run()
    {
        var documentA = await CreateDocumentAndRunAsync(CompanyA);
        var documentB = await CreateDocumentAndRunAsync(CompanyB);
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var version = await RowVersionAsync(client, documentA);
        (await DeleteAsync(client, documentA, version)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ActiveRunCountAsync(documentA)).ShouldBe(0);
        (await ActiveRunCountAsync(documentB)).ShouldBe(1,
            "another tenant's run must be untouched by the deletion");

        // Tenant B's document is still deletable by B and still owns its run.
        using var clientB = ClientFor(host, EditorB);
        var versionB = await RowVersionAsync(clientB, documentB);
        (await DeleteAsync(clientB, documentB, versionB)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ActiveRunCountAsync(documentB)).ShouldBe(0);
    }

    [Fact]
    public async Task No_attempt_is_orphaned_by_the_delete()
    {
        var document = await CreateDocumentAndRunAsync(CompanyA);
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var queue = NewQueue();

        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();

        var version = await RowVersionAsync(client, document);
        (await DeleteAsync(client, document, version)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Every attempt still points at a run that exists; the foreign key and the cancel CTE agree.
        (await ScalarAsync(
            "select count(*) from platform.processing_attempt a " +
            "left join platform.processing_run r on r.id = a.run_id " +
            "where r.id is null"))
            .ShouldBe("0");
        (await ScalarAsync(
            $"select count(*) from platform.processing_attempt where run_id = '{job!.RunId}' and status <> 'Cancelled'::platform.processing_status"))
            .ShouldBe("0");
    }

    private ProcessingQueue NewQueue() => new(
        fixture.ConnectionString,
        new WorkerOptions { LeaseSeconds = 60, MaxAttempts = 3, BackoffSeconds = [0] });

    private static HttpClient ClientFor(ApiFactory host, Guid subject)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(subject));
        return client;
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid documentId, uint rowVersion)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/documents/{documentId}", UriKind.Relative));
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{rowVersion}\""));
        return await client.SendAsync(request);
    }

    private static async Task<uint> RowVersionAsync(HttpClient client, Guid documentId)
    {
        var response = await client.GetAsync(new Uri($"/documents/{documentId}", UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var detail = await response.Content.ReadFromJsonAsync<DocumentDetail>();
        detail.ShouldNotBeNull();
        return detail!.RowVersion;
    }

    private async Task<Guid> CreateDocumentAndRunAsync(Guid company)
    {
        var document = Guid.NewGuid();
        var run = Guid.NewGuid();
        var user = company == CompanyA ? EditorA : EditorB;

        await ExecuteAsync(
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{document}', 'race/{document:N}.pdf', 'race.pdf', 'application/pdf', 1024, '{user}', '{company}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values ('{run}', '{document}', '{company}');");

        _documents.Add(document);
        return document;
    }

    private async Task<int> ActiveRunCountAsync(Guid document) => Convert.ToInt32(
        await ScalarAsync(
            $"select count(*) from platform.processing_run where document_id = '{document}' " +
            "and status in ('Queued'::platform.processing_status, 'Running'::platform.processing_status)"));

    private async Task<int> RunningAttemptCountAsync(Guid runId) => Convert.ToInt32(
        await ScalarAsync(
            $"select count(*) from platform.processing_attempt where run_id = '{runId}' " +
            "and status = 'Running'::platform.processing_status"));

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
