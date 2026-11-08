using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Convenciones.Clauses;
using Convenciones.Worker;
using Convenciones.Worker.Extraction;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

[Collection(DocumentsCollection.Name)]
public sealed class ClassificationReviewTests(DocumentsFixture fixture) : IAsyncLifetime
{
    private static readonly Guid EditorA = new("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid EditorB = new("b1000000-0000-0000-0000-000000000002");
    private static readonly Guid CompanyA = new("b2000000-0000-0000-0000-000000000001");

    private readonly List<(Guid Run, Guid Document)> _created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var document in _created.Select(item => item.Document).Distinct())
        {
            await ExecuteAsync($"delete from platform.processing_run where document_id = '{document}';");
            await ExecuteAsync($"delete from platform.document where id = '{document}';");
        }
    }

    [Fact]
    public async Task Clauses_list_includes_persisted_classifications_without_n_plus_one()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var response = await client.GetAsync($"/documents/{documentId}/clauses");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var classification = json.RootElement
            .GetProperty("items")[0]
            .GetProperty("classifications")[0];

        classification.GetProperty("key").GetString().ShouldBe("pricing");
        classification.GetProperty("name").GetString().ShouldBe("Precio y pago");
        classification.GetProperty("source").GetString().ShouldBe("ai");
        classification.GetProperty("confidence").GetDouble().ShouldBe(0.9);
        json.RootElement.GetProperty("items")[0]
            .GetProperty("classificationVersion").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Human_correction_keeps_the_original_ai_row_and_appends_an_audit_record()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var version = await GetVersionAsync(client, clauseId);
        var request = new
        {
            status = "Corrected",
            classifications = new[]
            {
                new { key = "scope", confidence = 1.0, rationale = "Corrección humana" },
            },
            primary = "scope",
            expectedVersion = version,
        };
        var response = await client.PatchAsJsonAsync($"/clauses/{clauseId}", request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var classification = json.RootElement.GetProperty("classifications")[0];
        classification.GetProperty("key").GetString().ShouldBe("scope");
        classification.GetProperty("source").GetString().ShouldBe("human");
        classification.GetProperty("isPrimary").GetBoolean().ShouldBeTrue();
        json.RootElement.GetProperty("classificationVersion").GetString().ShouldNotBe(version);

        // The AI row is untouched: the correction lives in the append-only audit trail.
        (await ScalarAsync(
            $"select category.key from platform.clause_classification c " +
            $"join platform.clause_category category on category.id = c.category_id " +
            $"where c.clause_id = '{clauseId}'")).ShouldBe("pricing");

        var changes = await ScalarAsync(
            $"select changes::text from platform.audit_entry " +
            $"where entity_id = '{clauseId}' and action = 'clause.classification.corrected' " +
            $"order by id desc limit 1");
        changes.ShouldContain("previous");
        changes.ShouldContain("next");
        changes.ShouldContain("scope");

        var auditActor = await ScalarAsync(
            $"select actor_id::text from platform.audit_entry " +
            $"where entity_id = '{clauseId}' and action = 'clause.classification.corrected' " +
            $"order by id desc limit 1");
        auditActor.ShouldBe(EditorA.ToString());

        // The public GET path uses the same projection as the list.
        var detail = await client.GetAsync($"/clauses/{clauseId}");
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        detailJson.RootElement.GetProperty("classifications")[0]
            .GetProperty("source").GetString().ShouldBe("human");
    }

    [Fact]
    public async Task Correction_with_stale_version_is_rejected_without_a_second_audit_event()
    {
        var (_, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var version = await GetVersionAsync(client, clauseId);
        var first = new
        {
            status = "Corrected",
            classifications = new[] { new { key = "scope", confidence = 1.0 } },
            primary = "scope",
            expectedVersion = version,
        };
        (await client.PatchAsJsonAsync($"/clauses/{clauseId}", first)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        var stale = new
        {
            status = "Corrected",
            classifications = new[] { new { key = "pricing", confidence = 1.0 } },
            primary = "pricing",
            expectedVersion = version,
        };
        var response = await client.PatchAsJsonAsync($"/clauses/{clauseId}", stale);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().ShouldBe("classification_version_conflict");

        (await ScalarAsync(
            $"select count(*) from platform.audit_entry " +
            $"where entity_id = '{clauseId}' and action = 'clause.classification.corrected'"))
            .ShouldBe("1");
        (await ScalarAsync(
            $"select category.key from platform.clause_classification c " +
            $"join platform.clause_category category on category.id = c.category_id " +
            $"where c.clause_id = '{clauseId}'")).ShouldBe("pricing");
    }

    [Fact]
    public async Task Ai_reclassification_changes_the_version_and_rejects_a_stale_human_edit()
    {
        var (_, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var version = await GetVersionAsync(client, clauseId);
        await ExecuteAsync(
            $"delete from platform.clause_classification where clause_id = '{clauseId}';" +
            $"insert into platform.clause_classification " +
            $"(clause_id, category_id, company_id, confidence, is_primary, model) " +
            $"select '{clauseId}', id, 'b2000000-0000-0000-0000-000000000001', 0.8, true, 'reclassified' " +
            $"from platform.clause_category where key = 'scope';");

        var newVersion = await GetVersionAsync(client, clauseId);
        newVersion.ShouldNotBe(version);

        var staleCorrection = new
        {
            status = "Corrected",
            classifications = new[] { new { key = "pricing", confidence = 1.0 } },
            primary = "pricing",
            expectedVersion = version,
        };
        var response = await client.PatchAsJsonAsync($"/clauses/{clauseId}", staleCorrection);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry " +
            $"where entity_id = '{clauseId}' and action = 'clause.classification.corrected'"))
            .ShouldBe("0");
    }

    [Fact]
    public async Task Ai_reclassification_changes_the_version_even_when_a_human_correction_wins()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var version = await GetVersionAsync(client, clauseId);

        (await client.PatchAsJsonAsync($"/clauses/{clauseId}", new
        {
            status = "Corrected",
            classifications = new[] { new { key = "scope", confidence = 1.0 } },
            primary = "scope",
            expectedVersion = version,
        })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var humanVersion = await GetVersionAsync(client, clauseId);
        await ExecuteAsync(
            $"delete from platform.clause_classification where clause_id = '{clauseId}';" +
            $"insert into platform.clause_classification " +
            $"(clause_id, category_id, company_id, confidence, is_primary, model) " +
            $"select '{clauseId}', id, '{CompanyA}', 0.6, true, 'reclassified' " +
            $"from platform.clause_category where key = 'pricing';");

        var afterAi = await GetVersionAsync(client, clauseId);
        afterAi.ShouldNotBe(humanVersion);

        var list = await client.GetAsync($"/documents/{documentId}/clauses");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var classification = listJson.RootElement.GetProperty("items")[0].GetProperty("classifications")[0];
        classification.GetProperty("key").GetString().ShouldBe("scope");
        classification.GetProperty("source").GetString().ShouldBe("human");
    }

    [Fact]
    public async Task Identical_classification_state_keeps_the_same_version()
    {
        var (_, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var first = await GetVersionAsync(client, clauseId);
        var second = await GetVersionAsync(client, clauseId);

        second.ShouldBe(first);
    }

    [Fact]
    public async Task Reextraction_refuses_to_replace_a_clause_with_a_human_correction()
    {
        var (documentId, clauseId, runId) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var version = await GetVersionAsync(client, clauseId);
        (await client.PatchAsJsonAsync($"/clauses/{clauseId}", new
        {
            status = "Corrected",
            classifications = new[] { new { key = "scope", confidence = 1.0 } },
            primary = "scope",
            expectedVersion = version,
        })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var job = await ActivateRunAsync(runId, documentId);
        var replacement = new MergedClause(
            1, null, null, "Cláusula reextraída", 1, 1, 0.9, false);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            store.SaveClausesAsync(job, [replacement], CancellationToken.None));

        (await ScalarAsync(
            $"select count(*) from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe("1");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry " +
            $"where entity_id = '{clauseId}' and action = 'clause.classification.corrected'"))
            .ShouldBe("1");
    }

    [Fact]
    public async Task Purge_waits_for_the_document_lock()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        await using var lockConnection = new NpgsqlConnection(fixture.ConnectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock_shared(hashtextextended(@key, 0));",
            lockConnection,
            lockTransaction))
        {
            command.Parameters.AddWithValue("key", ClassificationLocks.DocumentKey(documentId));
            await command.ExecuteNonQueryAsync();
        }

        var retention = new Convenciones.Api.Infrastructure.ProcessingRetentionService(
            fixture.ConnectionString);
        var purge = retention.PurgeDocumentAsync(documentId, CancellationToken.None);
        await Task.Delay(300);
        purge.IsCompleted.ShouldBeFalse();

        await lockTransaction.CommitAsync();
        await purge;

        (await ScalarAsync($"select count(*) from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe("0");
    }

    [Fact]
    public async Task Purge_refuses_to_delete_a_document_with_human_corrections()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var version = await GetVersionAsync(client, clauseId);
        (await client.PatchAsJsonAsync($"/clauses/{clauseId}", new
        {
            status = "Corrected",
            classifications = new[] { new { key = "scope", confidence = 1.0 } },
            primary = "scope",
            expectedVersion = version,
        })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var retention = new Convenciones.Api.Infrastructure.ProcessingRetentionService(
            fixture.ConnectionString);
        await Should.ThrowAsync<Convenciones.Api.Infrastructure.ProcessingRetentionService.ClassificationCorrectionsPresentException>(
            () => retention.PurgeDocumentAsync(documentId, CancellationToken.None));

        (await ScalarAsync($"select count(*) from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe("1");
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{clauseId}' " +
            $"and action = 'clause.classification.corrected'")).ShouldBe("1");
    }

    [Fact]
    public async Task Two_concurrent_corrections_based_on_the_same_version_cannot_both_win()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var hostA = new ApiFactory(fixture.ConnectionString);
        using var hostB = new ApiFactory(fixture.ConnectionString);
        using var clientA = ClientFor(hostA, EditorA);
        using var clientB = ClientFor(hostB, EditorA);

        var version = await GetVersionAsync(clientA, clauseId);
        var requestA = new
        {
            status = "Corrected",
            text = "Correction A",
            heading = "Heading A",
            classifications = new[] { new { key = "scope", confidence = 1.0 } },
            primary = "scope",
            expectedVersion = version,
        };
        var requestB = new
        {
            status = "Corrected",
            text = "Correction B",
            heading = "Heading B",
            classifications = new[] { new { key = "pricing", confidence = 1.0 } },
            primary = "pricing",
            expectedVersion = version,
        };

        await using var lockConnection = new NpgsqlConnection(fixture.ConnectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0));", lockConnection, lockTransaction))
        {
            command.Parameters.AddWithValue("key", ClassificationLocks.ClauseKey(clauseId));
            await command.ExecuteNonQueryAsync();
        }

        var taskA = clientA.PatchAsJsonAsync($"/clauses/{clauseId}", requestA);
        var taskB = clientB.PatchAsJsonAsync($"/clauses/{clauseId}", requestB);
        await WaitForBlockedSessionsAsync(lockConnection.ProcessID, 2);
        await lockTransaction.CommitAsync();
        var responses = await Task.WhenAll(taskA, taskB);

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry " +
            $"where entity_id = '{clauseId}' and action = 'clause.classification.corrected'"))
            .ShouldBe("1");
        (await ScalarAsync(
            $"select review_status::text from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe("Corrected");
        (await ScalarAsync(
            $"select count(*) from platform.clause_classification where clause_id = '{clauseId}'"))
            .ShouldBe("1");

        var finalVersion = await GetVersionAsync(clientA, clauseId);
        finalVersion.ShouldNotBe(version);
        var finalList = await clientA.GetAsync($"/documents/{documentId}/clauses");
        using var finalJson = JsonDocument.Parse(await finalList.Content.ReadAsStringAsync());
        finalJson.RootElement.GetProperty("items")[0]
            .GetProperty("classificationVersion").GetString().ShouldBe(finalVersion);
        var winner = responses[0].StatusCode == HttpStatusCode.OK ? "scope" : "pricing";
        var winningSuffix = responses[0].StatusCode == HttpStatusCode.OK ? "A" : "B";
        finalJson.RootElement.GetProperty("items")[0]
            .GetProperty("classifications")[0].GetProperty("key").GetString().ShouldBe(winner);
        (await ScalarAsync($"select text from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe($"Correction {winningSuffix}");
        (await ScalarAsync($"select heading from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe($"Heading {winningSuffix}");
        (await ScalarAsync(
            $"select changes->>'primary' from platform.audit_entry where entity_id = '{clauseId}' " +
            "and action = 'clause.classification.corrected'")).ShouldBe(winner);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Normal_review_with_consistent_audit_updates_status()
    {
        var (_, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var response = await client.PatchAsJsonAsync(
            $"/clauses/{clauseId}",
            new { status = "Accepted", text = "Texto revisado", heading = "Encabezado revisado" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScalarAsync(
            $"select review_status::text from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe("Accepted");
        (await ScalarAsync(
            $"select text from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe("Texto revisado");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_does_not_persist_when_audit_is_inconsistent(bool correction)
    {
        var (_, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var version = await GetVersionAsync(client, clauseId);
        await ExecuteAsync(
            "insert into platform.audit_entry " +
            "(actor_id, module, entity_type, entity_id, action, changes) values " +
            $"('{EditorA}', 'documents', 'extracted_clause', '{clauseId}', " +
            "'clause.classification.corrected', '{\"next\":\"not-an-array\"}'::jsonb);");

        var beforeAudit = await ScalarAsync($"select count(*) from platform.audit_entry where entity_id = '{clauseId}'");
        var beforeClassifications = await ScalarAsync(
            $"select coalesce(jsonb_agg(to_jsonb(c) order by c.id)::text, '[]') from platform.clause_classification c where c.clause_id = '{clauseId}'");
        var response = correction
            ? await client.PatchAsJsonAsync($"/clauses/{clauseId}", new
            {
                status = "Corrected",
                text = "Changed text",
                heading = "Changed heading",
                classifications = new[] { new { key = "scope", confidence = 1.0 } },
                primary = "scope",
                expectedVersion = version,
            })
            : await client.PatchAsJsonAsync(
                $"/clauses/{clauseId}",
                new { status = "Accepted", text = "Changed text", heading = "Changed heading" });

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await ScalarAsync(
            $"select review_status::text from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe("Pending");
        (await ScalarAsync(
            $"select text from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe("Cláusula de prueba");
        (await ScalarAsync(
            $"select coalesce(heading, '') from platform.extracted_clause where id = '{clauseId}'"))
            .ShouldBe(string.Empty);
        (await ScalarAsync($"select count(*) from platform.audit_entry where entity_id = '{clauseId}'"))
            .ShouldBe(beforeAudit);
        (await ScalarAsync(
            $"select coalesce(jsonb_agg(to_jsonb(c) order by c.id)::text, '[]') from platform.clause_classification c where c.clause_id = '{clauseId}'"))
            .ShouldBe(beforeClassifications);
        (await client.GetAsync($"/clauses/{clauseId}")).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Malformed_latest_correction_fails_closed_instead_of_returning_ai()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        await ExecuteAsync(
            "insert into platform.audit_entry " +
            "(actor_id, module, entity_type, entity_id, action, changes) values " +
            $"('{EditorA}', 'documents', 'extracted_clause', '{clauseId}', " +
            "'clause.classification.corrected', '{\"next\":\"not-an-array\"}'::jsonb);");

        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var response = await client.GetAsync($"/documents/{documentId}/clauses");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().ShouldBe("classification_audit_inconsistent");
    }

    [Fact]
    public async Task The_latest_correction_wins_deterministically_after_a_versioned_update()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var initialVersion = await GetVersionAsync(client, clauseId);
        var first = new
        {
            status = "Corrected",
            classifications = new[] { new { key = "scope", confidence = 1.0 } },
            primary = "scope",
            expectedVersion = initialVersion,
        };
        var firstResponse = await client.PatchAsJsonAsync($"/clauses/{clauseId}", first);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var version = firstJson.RootElement.GetProperty("classificationVersion").GetString();

        var second = new
        {
            status = "Corrected",
            classifications = new[] { new { key = "pricing", confidence = 1.0 } },
            primary = "pricing",
            expectedVersion = version,
        };
        (await client.PatchAsJsonAsync($"/clauses/{clauseId}", second)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        var list = await client.GetAsync($"/documents/{documentId}/clauses");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var classification = listJson.RootElement.GetProperty("items")[0];
        classification.GetProperty("classifications")[0]
            .GetProperty("key").GetString().ShouldBe("pricing");
        classification.GetProperty("classifications")[0]
            .GetProperty("source").GetString().ShouldBe("human");
        classification.GetProperty("classificationVersion").GetString().ShouldNotBe(version);

        (await ScalarAsync(
            $"select count(*) from platform.audit_entry " +
            $"where entity_id = '{clauseId}' and action = 'clause.classification.corrected'"))
            .ShouldBe("2");
    }

    [Fact]
    public async Task Worker_reclassification_and_human_correction_are_serialized()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var version = await GetVersionAsync(client, clauseId);
        var scopeId = Guid.Parse(await ScalarAsync(
            "select id::text from platform.clause_category where key = 'scope'"));
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var (job, workerClauseId) = await CreateActiveWorkerAsync(documentId);
        var assignment = new ClassificationAssignment(workerClauseId, scopeId, 0.7, true, "AI rerun");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Worker()
        {
            await gate.Task;
            await store.SaveClassificationsAsync(job, [assignment], 0, CancellationToken.None);
        }

        async Task<HttpResponseMessage> Human()
        {
            await gate.Task;
            return await client.PatchAsJsonAsync($"/clauses/{clauseId}", new
            {
                status = "Corrected",
                classifications = new[] { new { key = "pricing", confidence = 1.0 } },
                primary = "pricing",
                expectedVersion = version,
            });
        }

        var workerTask = Worker();
        var humanTask = Human();
        gate.SetResult();
        await Task.WhenAll(workerTask, humanTask);
        using var response = await humanTask;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var auditCount = await ScalarAsync(
            $"select count(*) from platform.audit_entry " +
            $"where entity_id = '{clauseId}' and action = 'clause.classification.corrected'");
        auditCount.ShouldBe("1");
        var list = await client.GetAsync($"/documents/{documentId}/clauses");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        listJson.RootElement.GetProperty("items")[0]
            .GetProperty("classifications")[0]
            .GetProperty("source").GetString().ShouldBe("human");
        (await ScalarAsync(
            $"select c.key from platform.clause_classification cc " +
            $"join platform.clause_category c on c.id = cc.category_id " +
            $"where cc.clause_id = '{workerClauseId}'")).ShouldBe("scope");
    }
    [Fact]
    public async Task Corrections_for_different_clauses_do_not_serialize_on_each_other()
    {
        var (_, firstClause, _) = await CreateClauseAsync();
        var (_, secondClause, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var firstVersion = await GetVersionAsync(client, firstClause);
        var secondVersion = await GetVersionAsync(client, secondClause);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Send(Guid clause, string version, string key)
        {
            await gate.Task;
            return await client.PatchAsJsonAsync($"/clauses/{clause}", new
            {
                status = "Corrected",
                classifications = new[] { new { key, confidence = 1.0 } },
                primary = key,
                expectedVersion = version,
            });
        }

        var firstTask = Send(firstClause, firstVersion, "scope");
        var secondTask = Send(secondClause, secondVersion, "pricing");
        gate.SetResult();
        using var firstResponse = await firstTask;
        using var secondResponse = await secondTask;

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry " +
            $"where action = 'clause.classification.corrected' and entity_id in " +
            $"('{firstClause}', '{secondClause}')")).ShouldBe("2");
    }

    [Fact]
    public async Task Semantically_invalid_correction_payloads_fail_closed()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        var payloads = new[]
        {
            """{"next":[],"primary":"scope"}""",
            """{"next":[{"key":"scope","confidence":1,"isPrimary":true},{"key":"scope","confidence":1,"isPrimary":false}],"primary":"scope"}""",
            """{"next":[{"key":"unknown","confidence":1,"isPrimary":true}],"primary":"unknown"}""",
            """{"next":[{"key":"scope","confidence":2,"isPrimary":true}],"primary":"scope"}""",
            """{"next":[{"key":"scope","confidence":"1","isPrimary":true}],"primary":"scope"}""",
            """{"next":[{"key":"scope","confidence":1,"isPrimary":true},{"key":"pricing","confidence":1,"isPrimary":true}],"primary":"scope"}""",
            """{"next":[{"key":"scope","confidence":1,"isPrimary":false}],"primary":"scope"}""",
            """{"next":[{"key":"scope","isPrimary":true}],"primary":"scope"}""",
            """{"next":[{"key":"scope","confidence":1,"isPrimary":"yes"}],"primary":"scope"}""",
        };

        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        foreach (var payload in payloads)
        {
            await InsertCorrectionAuditAsync(clauseId, payload);
            var response = await client.GetAsync($"/documents/{documentId}/clauses");
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("code").GetString().ShouldBe("classification_audit_inconsistent");
        }
    }

    [Fact]
    public async Task Invalid_latest_entry_after_a_valid_correction_fails_closed()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var version = await GetVersionAsync(client, clauseId);

        var valid = new
        {
            status = "Corrected",
            classifications = new[] { new { key = "scope", confidence = 1.0 } },
            primary = "scope",
            expectedVersion = version,
        };
        (await client.PatchAsJsonAsync($"/clauses/{clauseId}", valid)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        await InsertCorrectionAuditAsync(clauseId, """{"next":[],"primary":"scope"}""");
        var response = await client.GetAsync($"/documents/{documentId}/clauses");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().ShouldBe("classification_audit_inconsistent");
    }

    [Fact]
    public async Task Latest_correction_uses_occurred_at_then_id()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        await InsertCorrectionAuditAsync(
            clauseId,
            """{"next":[{"key":"scope","confidence":1,"isPrimary":true}],"primary":"scope"}""",
            "2026-01-01T00:00:00Z");
        await InsertCorrectionAuditAsync(
            clauseId,
            """{"next":[{"key":"pricing","confidence":1,"isPrimary":true}],"primary":"pricing"}""",
            "2026-01-02T00:00:00Z");

        var list = await client.GetAsync($"/documents/{documentId}/clauses");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("items")[0]
            .GetProperty("classifications")[0]
            .GetProperty("key").GetString().ShouldBe("pricing");
    }

    [Fact]
    public async Task Latest_correction_with_equal_timestamps_uses_id_tie_breaker()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        const string timestamp = "2026-01-01T00:00:00Z";

        await InsertCorrectionAuditAsync(
            clauseId,
            """{"next":[{"key":"scope","confidence":1,"isPrimary":true}],"primary":"scope"}""",
            timestamp);
        await InsertCorrectionAuditAsync(
            clauseId,
            """{"next":[{"key":"pricing","confidence":1,"isPrimary":true}],"primary":"pricing"}""",
            timestamp);

        var list = await client.GetAsync($"/documents/{documentId}/clauses");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("items")[0]
            .GetProperty("classifications")[0]
            .GetProperty("key").GetString().ShouldBe("pricing");
    }

    [Fact]
    public async Task Listing_uses_a_constant_number_of_sql_commands()
    {
        var (documentId, _, runId) = await CreateClauseAsync();
        var counter = new CommandCountingLoggerProvider();
        using var host = new ApiFactory(fixture.ConnectionString, loggerProvider: counter);
        using var client = ClientFor(host, EditorA);

        var before = counter.Count;
        (await client.GetAsync($"/documents/{documentId}/clauses")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var oneClauseCommands = counter.Count - before;

        var secondClauseId = Guid.NewGuid();
        await ExecuteAsync(
            $"insert into platform.extracted_clause " +
            $"(id, run_id, document_id, company_id, ordinal, text, page_from, page_to) " +
            $"values ('{secondClauseId}', '{runId}', '{documentId}', '{CompanyA}', 2, 'Segunda cláusula', 1, 1);" +
            $"insert into platform.clause_classification " +
            $"(clause_id, category_id, company_id, confidence, is_primary, model, prompt_version) " +
            $"select '{secondClauseId}', id, '{CompanyA}', 0.8, true, 'fake', 'classification/v1' " +
            $"from platform.clause_category where key = 'scope';");

        var secondBefore = counter.Count;
        (await client.GetAsync($"/documents/{documentId}/clauses")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var twoClauseCommands = counter.Count - secondBefore;

        twoClauseCommands.ShouldBe(oneClauseCommands);
    }

    [Fact]
    public async Task Review_reloads_the_clause_after_waiting_for_the_advisory_lock()
    {
        var (_, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        await using var lockConnection = new NpgsqlConnection(fixture.ConnectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@key, 0));",
            lockConnection,
            lockTransaction))
        {
            command.Parameters.AddWithValue("key", ClassificationLocks.ClauseKey(clauseId));
            await command.ExecuteNonQueryAsync();
        }

        var review = client.PatchAsJsonAsync($"/clauses/{clauseId}", new { status = "Accepted" });
        await WaitForBlockedSessionsAsync(lockConnection.ProcessID, 1);
        await ExecuteAsync(
            $"update platform.extracted_clause set text = 'Texto actualizado por otra operación' " +
            $"where id = '{clauseId}';");
        await lockTransaction.CommitAsync();

        var response = await review;
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("text").GetString()
            .ShouldBe("Texto actualizado por otra operación");
    }

    [Fact]
    public async Task The_most_recent_completed_run_is_the_current_run()
    {
        var documentId = await CreateDocumentAsync();
        var first = await AddRunClauseAsync(
            documentId, "Completed", DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "Clasificación antigua");
        var second = await AddRunClauseAsync(
            documentId, "Completed", DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"), "Clasificación vigente", "scope");

        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var list = await client.GetAsync($"/documents/{documentId}/clauses");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        items[0].GetProperty("id").GetGuid().ShouldBe(second.ClauseId);
        items[0].GetProperty("text").GetString().ShouldBe("Clasificación vigente");

        (await client.GetAsync($"/clauses/{first.ClauseId}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/clauses/{second.ClauseId}")).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        var version = await GetVersionAsync(client, second.ClauseId);
        var stale = await client.PatchAsJsonAsync($"/clauses/{first.ClauseId}", new
        {
            status = "Corrected",
            classifications = new[] { new { key = "pricing", confidence = 1.0 } },
            primary = "pricing",
            expectedVersion = version,
        });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{first.ClauseId}'"))
            .ShouldBe("0");
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task An_incomplete_run_does_not_replace_the_last_completed_run(string status)
    {
        var documentId = await CreateDocumentAsync();
        var completed = await AddRunClauseAsync(
            documentId, "Completed", DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "Completada");
        var running = await AddRunClauseAsync(
            documentId, status, null,
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"), "En ejecución", "scope");

        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var list = await client.GetAsync($"/documents/{documentId}/clauses");
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("items")[0]
            .GetProperty("id").GetGuid().ShouldBe(completed.ClauseId);
        (await client.GetAsync($"/clauses/{running.ClauseId}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Equal_completion_times_use_created_at_then_id_as_tie_breakers()
    {
        var documentId = await CreateDocumentAsync();
        var finished = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var first = await AddRunClauseAsync(
            documentId, "Completed", finished, finished, "Antigua");
        var second = await AddRunClauseAsync(
            documentId, "Completed", finished, finished.AddMinutes(1), "Vigente", "scope");

        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var list = await client.GetAsync($"/documents/{documentId}/clauses");
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("items")[0]
            .GetProperty("id").GetGuid().ShouldBe(second.ClauseId);
        (await client.GetAsync($"/clauses/{first.ClauseId}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Expected_version_changes_when_the_current_run_changes()
    {
        var documentId = await CreateDocumentAsync();
        var first = await AddRunClauseAsync(
            documentId, "Completed", DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "Primera");
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var firstVersion = await GetVersionAsync(client, first.ClauseId);

        var second = await AddRunClauseAsync(
            documentId, "Completed", DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"), "Segunda", "scope");
        var secondVersion = await GetVersionAsync(client, second.ClauseId);

        secondVersion.ShouldNotBe(firstVersion);
        (await client.GetAsync($"/clauses/{first.ClauseId}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Equal_completion_and_creation_times_use_id_as_the_final_tie_breaker()
    {
        var documentId = await CreateDocumentAsync();
        var instant = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await AddRunClauseAsync(documentId, "Completed", instant, instant, "Primera");
        await AddRunClauseAsync(documentId, "Completed", instant, instant, "Segunda", "scope");

        var expected = Guid.Parse(await ScalarAsync(
            $"select id::text from platform.processing_run where document_id = '{documentId}' " +
            $"order by finished_at desc, created_at desc, id desc limit 1"));

        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        var list = await client.GetAsync($"/documents/{documentId}/clauses");
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var currentClause = json.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
        var currentRun = Guid.Parse(await ScalarAsync(
            $"select run_id::text from platform.extracted_clause where id = '{currentClause}'"));
        currentRun.ShouldBe(expected);
    }

    [Fact]
    public async Task No_completed_run_returns_no_current_clauses()
    {
        var documentId = await CreateDocumentAsync();
        var queued = await AddRunClauseAsync(
            documentId, "Queued", null, DateTimeOffset.UtcNow, "No vigente");

        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);

        var list = await client.GetAsync($"/documents/{documentId}/clauses");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("total").GetInt32().ShouldBe(0);
        json.RootElement.GetProperty("items").GetArrayLength().ShouldBe(0);
        (await client.GetAsync($"/clauses/{queued.ClauseId}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.PatchAsJsonAsync($"/clauses/{queued.ClauseId}", new { status = "Accepted" }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{queued.ClauseId}'"))
            .ShouldBe("0");
    }

    [Fact]
    public async Task Deleted_documents_do_not_expose_or_accept_clause_changes()
    {
        var (documentId, clauseId, runId) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host, EditorA);
        await ExecuteAsync(
            $"update platform.document set deleted_at = now() where id = '{documentId}';");

        (await client.GetAsync($"/documents/{documentId}/clauses")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/clauses/{clauseId}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/processing/{runId}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.PatchAsJsonAsync($"/clauses/{clauseId}", new { status = "Accepted" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ScalarAsync(
            $"select count(*) from platform.audit_entry where entity_id = '{clauseId}' " +
            $"and action = 'clause.classification.corrected'")).ShouldBe("0");
    }

    [Fact]
    public async Task Another_company_cannot_read_or_correct_the_clause()
    {
        var (documentId, clauseId, _) = await CreateClauseAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var other = ClientFor(host, EditorB);

        (await other.GetAsync($"/documents/{documentId}/clauses")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);

        var request = new
        {
            status = "Corrected",
            classifications = new[] { new { key = "scope", confidence = 1.0 } },
            primary = "scope",
        };
        (await other.PatchAsJsonAsync($"/clauses/{clauseId}", request)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);

        (await ScalarAsync(
            $"select count(*) from platform.audit_entry " +
            $"where entity_id = '{clauseId}' and action = 'clause.classification.corrected'"))
            .ShouldBe("0");
    }

    private async Task<(ProcessingJob Job, Guid ClauseId)> CreateActiveWorkerAsync(Guid documentId)
    {
        var runId = Guid.NewGuid();
        var clauseId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        await ExecuteAsync(
            $"""
            insert into platform.processing_run
                (id, document_id, company_id, status, attempts, started_at)
            values
                ('{runId}', '{documentId}', '{CompanyA}', 'Running', 1, now());
            insert into platform.extracted_clause
                (id, run_id, document_id, company_id, ordinal, text, page_from, page_to)
            values
                ('{clauseId}', '{runId}', '{documentId}', '{CompanyA}', 1, 'Cláusula de worker', 1, 1);
            insert into platform.processing_attempt
                (id, run_id, company_id, attempt_no, status)
            values
                ('{attemptId}', '{runId}', '{CompanyA}', 1, 'Running');
            """);

        _created.Add((runId, documentId));
        return (new ProcessingJob(runId, documentId, CompanyA, 1, attemptId), clauseId);
    }

    private async Task<ProcessingJob> ActivateRunAsync(Guid runId, Guid documentId)
    {
        var attemptId = Guid.NewGuid();
        await ExecuteAsync(
            $"update platform.processing_run set status = 'Running', attempts = 1, " +
            $"started_at = now(), updated_at = now() where id = '{runId}';" +
            $"insert into platform.processing_attempt (id, run_id, company_id, attempt_no, status) " +
            $"values ('{attemptId}', '{runId}', '{CompanyA}', 1, 'Running');");
        return new ProcessingJob(runId, documentId, CompanyA, 1, attemptId);
    }

    private async Task<(Guid RunId, Guid ClauseId)> AddRunClauseAsync(
        Guid documentId,
        string status,
        DateTimeOffset? finishedAt,
        DateTimeOffset createdAt,
        string text,
        string categoryKey = "pricing")
    {
        var runId = Guid.NewGuid();
        var clauseId = Guid.NewGuid();
        var finished = finishedAt is null
            ? "null"
            : $"'{finishedAt.Value:O}'::timestamptz";
        await ExecuteAsync(
            $"""
            insert into platform.processing_run
                (id, document_id, company_id, status, created_at, finished_at)
            values
                ('{runId}', '{documentId}', '{CompanyA}', '{status}', '{createdAt:O}'::timestamptz, {finished});
            insert into platform.extracted_clause
                (id, run_id, document_id, company_id, ordinal, text, page_from, page_to)
            values
                ('{clauseId}', '{runId}', '{documentId}', '{CompanyA}', 1, '{text}', 1, 1);
            insert into platform.clause_classification
                (clause_id, category_id, company_id, confidence, is_primary, model, prompt_version)
            select '{clauseId}', id, '{CompanyA}', 0.9, true, 'fake', 'classification/v1'
            from platform.clause_category where key = '{categoryKey}';
            """);

        _created.Add((runId, documentId));
        return (runId, clauseId);
    }

    private async Task<Guid> CreateDocumentAsync()
    {
        var documentId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        await ExecuteAsync(
            $"insert into platform.document " +
            $"(id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{documentId}', 'run/{suffix}.pdf', 'run.pdf', 'application/pdf', 1024, " +
            $"'{EditorA}', '{CompanyA}');");
        _created.Add((Guid.Empty, documentId));
        return documentId;
    }

    private static async Task<string> GetVersionAsync(HttpClient client, Guid clauseId)
    {
        var response = await client.GetAsync($"/clauses/{clauseId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("classificationVersion").GetString()!;
    }

    private async Task WaitForBlockedSessionsAsync(int blockerPid, int count)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            await using var command = new NpgsqlCommand(
                """
                select count(distinct pid) from pg_stat_activity
                where @blocker = any(pg_blocking_pids(pid))
                  and wait_event_type = 'Lock' and wait_event = 'advisory';
                """, connection);
            command.Parameters.AddWithValue("blocker", blockerPid);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) >= count)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Expected {count} PostgreSQL sessions blocked by PID {blockerPid}.");
    }

    private async Task<(Guid DocumentId, Guid ClauseId, Guid RunId)> CreateClauseAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var documentId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var clauseId = Guid.NewGuid();

        await ExecuteAsync(
            $"""
            insert into platform.document
                (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id)
            values
                ('{documentId}', 'review/{suffix}.pdf', 'review.pdf', 'application/pdf', 1024, '{EditorA}', '{CompanyA}');
            insert into platform.processing_run (id, document_id, company_id, status, finished_at)
            values ('{runId}', '{documentId}', '{CompanyA}', 'Completed', now());
            insert into platform.extracted_clause
                (id, run_id, document_id, company_id, ordinal, text, page_from, page_to)
            values
                ('{clauseId}', '{runId}', '{documentId}', '{CompanyA}', 1, 'Cláusula de prueba', 1, 1);
            insert into platform.clause_classification
                (clause_id, category_id, company_id, confidence, is_primary, model, prompt_version)
            select '{clauseId}', id, '{CompanyA}', 0.9, true, 'fake', 'classification/v1'
            from platform.clause_category where key = 'pricing';
            """);

        _created.Add((runId, documentId));
        return (documentId, clauseId, runId);
    }

    private static HttpClient ClientFor(ApiFactory host, Guid subject)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(subject));
        return client;
    }

    private async Task InsertCorrectionAuditAsync(Guid clauseId, string changes)
        => await InsertCorrectionAuditAsync(clauseId, changes, null);

    private async Task InsertCorrectionAuditAsync(Guid clauseId, string changes, string? occurredAt)
    {
        var timestamp = occurredAt is null ? string.Empty : $"'{occurredAt}', ";
        await ExecuteAsync(
            "insert into platform.audit_entry " +
            $"(actor_id, module, entity_type, entity_id, action, { (occurredAt is null ? string.Empty : "occurred_at, ") }changes) values " +
            $"('{EditorA}', 'documents', 'extracted_clause', '{clauseId}', " +
            $"'clause.classification.corrected', {timestamp}'{changes.Replace("'", "''")}'::jsonb);");
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
