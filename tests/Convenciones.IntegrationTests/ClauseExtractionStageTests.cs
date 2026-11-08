using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Ai;
using Convenciones.Ai.DocumentIntelligence;
using Convenciones.Documents;
using Convenciones.Worker;
using Convenciones.Worker.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ClauseExtractionStageTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<(Guid Company, Guid User, Guid Document, Guid Run)> _created = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var item in _created)
        {
            await ExecuteAsync($"delete from platform.processing_run where id = '{item.Run}';");
            await ExecuteAsync($"delete from platform.document where id = '{item.Document}';");
            await ExecuteAsync($"delete from platform.user_account where id = '{item.User}';");
            await ExecuteAsync($"delete from app.company where id = '{item.Company}';");
        }
    }

    [Fact]
    public async Task Stage_persists_clauses_with_ordinals_and_updates_the_run()
    {
        var (job, _) = await CreateRunAsync();
        var stage = new ClauseExtractionStage(
            new FakeStorage(),
            new FakeLayout(),
            new FakeModel(),
            new NpgsqlClauseStore(fixture.ConnectionString),
            new ExtractionOptions { ChunkCharacters = 10_000, OverlapCharacters = 1_000, MaxChunks = 30 },
            NullLogger<ClauseExtractionStage>.Instance);

        await stage.ExecuteAsync(job, CancellationToken.None);

        var count = await ScalarAsync($"select count(*) from platform.extracted_clause where run_id = '{job.RunId}'");
        count.ShouldBe("2");
        var first = await ScalarAsync(
            $"select text from platform.extracted_clause where run_id = '{job.RunId}' and ordinal = 1");
        first.ShouldContain("Cláusula primera");
        var clauseCount = await ScalarAsync($"select clause_count::text from platform.processing_run where id = '{job.RunId}'");
        clauseCount.ShouldBe("2");
    }

    [Fact]
    public async Task Classification_persists_multi_label_and_unclassified_counts()
    {
        var (job, _) = await CreateRunAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var extraction = new ClauseExtractionStage(
            new FakeStorage(),
            new FakeLayout(),
            new FakeModel(),
            store,
            new ExtractionOptions { ChunkCharacters = 10_000, OverlapCharacters = 1_000, MaxChunks = 30 },
            NullLogger<ClauseExtractionStage>.Instance);
        var classification = new ClauseClassificationStage(
            new FakeClassificationModel(),
            store,
            new ClassificationOptions { BatchSize = 20 },
            NullLogger<ClauseClassificationStage>.Instance);

        await extraction.ExecuteAsync(job, CancellationToken.None);
        await classification.ExecuteAsync(job, CancellationToken.None);

        var rows = await ScalarAsync($"select count(*) from platform.clause_classification where company_id = '{job.CompanyId}'");
        rows.ShouldBe("1");
        var counts = await ScalarAsync(
            $"select classified_count::text || '/' || unclassified_count::text from platform.processing_run where id = '{job.RunId}'");
        counts.ShouldBe("1/1");
    }
    private async Task<(ProcessingJob Job, Guid DocumentId)> CreateRunAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var attemptId = Guid.NewGuid();
        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{companyId}', 'Stage test {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{userId}', 'stage-{suffix}@example.test', 'Stage Test');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{documentId}', 'stage/{suffix}.pdf', 'stage.pdf', 'application/pdf', 1024, '{userId}', '{companyId}');" +
            $"insert into platform.processing_run (id, document_id, company_id, status, attempts, started_at) " +
            $"values ('{runId}', '{documentId}', '{companyId}', 'Running', 1, now());" +
            $"insert into platform.processing_attempt (id, run_id, company_id, attempt_no, status) " +
            $"values ('{attemptId}', '{runId}', '{companyId}', 1, 'Running');");

        _created.Add((companyId, userId, documentId, runId));
        return (new ProcessingJob(runId, documentId, companyId, 1, attemptId), documentId);
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

    private sealed class FakeStorage : IDocumentStorage
    {
        public Task SaveAsync(string key, Stream content, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes("pdf")));

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeLayout : IDocumentLayoutClient
    {
        public Task<DocumentLayout> AnalyzeAsync(Stream pdf, CancellationToken cancellationToken)
        {
            var text = "Cláusula primera. Objeto del contrato.\nCláusula segunda. Jornada.";
            var normalized = TextNormalization.Normalize(text);
            return Task.FromResult(new DocumentLayout(
                1,
                [new LayoutPage(1, 612, 792, text, normalized.Text, normalized.OriginalOffsets, [], [])],
                false,
                text.Length,
                TimeSpan.Zero));
        }
    }

    private sealed class FakeModel : IAiModelClient
    {
        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
        {
            var clauses = new[]
            {
                new { number = (string?)"Primera", heading = (string?)"Objeto", text = "Cláusula primera. Objeto del contrato.", page_from = 1, page_to = 1, confidence = 0.9 },
                new { number = (string?)null, heading = (string?)null, text = "Cláusula segunda. Jornada.", page_from = 1, page_to = 1, confidence = 0.8 },
            };
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { clauses }));
            return Task.FromResult(new AiCompletionResult(
                document.RootElement.Clone(),
                "fake",
                "clauses",
                10,
                5,
                TimeSpan.Zero));
        }
    }

    private sealed class FakeClassificationModel : IAiModelClient
    {
        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
        {
            var payload = """
                {"classifications":[
                  {"clause_ordinal":1,"categories":[{"key":"pricing","confidence":0.9}],"primary":"pricing","rationale":"Pago"},
                  {"clause_ordinal":2,"categories":[]}
                ]}
                """;
            using var document = JsonDocument.Parse(payload);
            return Task.FromResult(new AiCompletionResult(document.RootElement.Clone(), "fake", "clauses", 10, 5, TimeSpan.Zero));
        }
    }
}
