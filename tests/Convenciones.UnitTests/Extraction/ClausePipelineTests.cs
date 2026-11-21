using System.Text;
using System.Text.Json;
using Convenciones.Ai;
using Convenciones.Ai.DocumentIntelligence;
using Convenciones.Documents;
using Convenciones.Worker;
using Convenciones.Worker.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Extraction;

public sealed class ClausePipelineTests
{
    private static ExtractionOptions SmallOptions => new()
    {
        ChunkCharacters = 10_000,
        OverlapCharacters = 1_000,
        MaxChunks = 30,
    };

    [Fact]
    public void Chunker_keeps_page_ranges_and_overlap()
    {
        var pages = Enumerable.Range(1, 3)
            .Select(number => Page(number, new string((char)('a' + number), 6_000)))
            .ToArray();
        var layout = new DocumentLayout(3, pages, false, 6_000, TimeSpan.Zero);

        var chunks = ClauseChunker.Chunk(layout, SmallOptions);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks[0].PageFrom.ShouldBe(1);
        chunks[^1].PageTo.ShouldBe(3);
        chunks[1].CharStart.ShouldBeLessThan(chunks[0].CharStart + 10_000);
    }

    [Fact]
    public void Parser_accepts_a_clause_without_number_or_heading()
    {
        using var document = JsonDocument.Parse(
            """{"clauses":[{"number":null,"heading":null,"text":"ClÃƒÂ¡usula sin tÃƒÂ­tulo","page_from":1,"page_to":1,"confidence":0.8}]}""");
        var chunk = new ClauseChunk(1, 1, 1, "text", 0);

        var parsed = ClauseResponseParser.Parse(document.RootElement, chunk);

        parsed.Count.ShouldBe(1);
        parsed[0].Number.ShouldBeNull();
        parsed[0].Heading.ShouldBeNull();
        parsed[0].Confidence.ShouldBe(0.8);
    }

    [Fact]
    public void Parser_rejects_confidence_outside_the_range()
    {
        using var document = JsonDocument.Parse(
            """{"clauses":[{"text":"ClÃƒÂ¡usula","page_from":1,"page_to":1,"confidence":1.2}]}""");
        var chunk = new ClauseChunk(1, 1, 1, "text", 0);

        Should.Throw<ClauseExtractionException>(() => ClauseResponseParser.Parse(document.RootElement, chunk));
    }

    [Fact]
    public void Merger_drops_overlap_duplicates_and_flags_conflicts()
    {
        var clauses = new[]
        {
            new ParsedClause("Primera", "Objeto", "ClÃƒÂ¡usula primera. Objeto.", 1, 1, 0.9),
            new ParsedClause("Primera", "Objeto", "ClÃƒÂ¡usula primera. Objeto.", 1, 1, 0.9),
            new ParsedClause("Primera", "Objeto", "ClÃƒÂ¡usula primera. Objeto distinto.", 1, 1, 0.8),
        };

        var merged = ClauseMerger.Merge(clauses);

        merged.Count.ShouldBe(2);
        merged[0].Ordinal.ShouldBe(1);
        merged[1].NeedsReview.ShouldBeTrue();
    }

    [Fact]
    public async Task Stage_extracts_and_persists_clauses()
    {
        var store = new FakeStore();
        var stage = CreateStage(store, layout: Page(1, "ClÃƒÂ¡usula primera. Objeto."), clauseCount: 2);

        await stage.ExecuteAsync(new ProcessingJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1), CancellationToken.None);

        store.Saved.Count.ShouldBe(2);
        store.Saved[0].Ordinal.ShouldBe(1);
        store.Saved[1].Ordinal.ShouldBe(2);
        store.Progress.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Stage_fails_scanned_pdf_with_ocr_required()
    {
        var stage = CreateStage(new FakeStore(), layout: Page(1, string.Empty), clauseCount: 0, requiresOcr: true);

        var exception = await Should.ThrowAsync<PermanentProcessingException>(
            () => stage.ExecuteAsync(new ProcessingJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1), CancellationToken.None));
        exception.Code.ShouldBe("ocr_required");
    }

    [Fact]
    public async Task Stage_fails_when_chunk_limit_is_exceeded()
    {
        var store = new FakeStore();
        var stage = CreateStage(store, layout: Page(1, new string('x', 50_000)), clauseCount: 1, maxChunks: 2);

        var exception = await Should.ThrowAsync<PermanentProcessingException>(
            () => stage.ExecuteAsync(new ProcessingJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1), CancellationToken.None));
        exception.Code.ShouldBe("document_too_large");
    }

    private static ClauseExtractionStage CreateStage(
        FakeStore store,
        LayoutPage layout,
        int clauseCount,
        bool requiresOcr = false,
        int maxChunks = 30)
    {
        var documentLayout = new DocumentLayout(1, [layout], requiresOcr, layout.Text.Length, TimeSpan.Zero);
        var options = new ExtractionOptions
        {
            ChunkCharacters = 10_000,
            OverlapCharacters = 1_000,
            MaxChunks = maxChunks,
        };

        return new ClauseExtractionStage(
            new FakeStorage(),
            new FakeLayout(documentLayout),
            new FakeModel(clauseCount),
            store,
            options,
            NullLogger<ClauseExtractionStage>.Instance);
    }

    private static LayoutPage Page(int number, string text)
    {
        var normalized = TextNormalization.Normalize(text);
        return new LayoutPage(number, 612, 792, text, normalized.Text, normalized.OriginalOffsets, [], []);
    }

    private sealed class FakeStorage : IDocumentStorage
    {
        public Task SaveAsync(string key, Stream content, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes("pdf")));

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeLayout(DocumentLayout layout) : IDocumentLayoutClient
    {
        public Task<DocumentLayout> AnalyzeAsync(Stream pdf, CancellationToken cancellationToken) =>
            Task.FromResult(layout);
    }

    private sealed class FakeModel(int clauseCount) : IAiModelClient
    {
        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
        {
            var clauses = Enumerable.Range(1, clauseCount)
                .Select(number => new
                {
                    number = number == 1 ? null : $"ClÃƒÂ¡usula {number}",
                    heading = number == 1 ? null : "Objeto",
                    text = $"ClÃƒÂ¡usula {number}. Texto.",
                    page_from = 1,
                    page_to = 1,
                    confidence = 0.9,
                });
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

    private sealed class FakeStore : IClauseStore
    {
        public List<MergedClause> Saved { get; } = [];

        public List<string> Progress { get; } = [];

        public List<StoredClause> Clauses { get; } = [];

        public List<ClauseCategoryInfo> Categories { get; } = [];

        public List<ClassificationAssignment> Classifications { get; } = [];

        public int Unclassified { get; private set; }

        public Task<StoredDocument?> LoadDocumentAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<StoredDocument?>(new StoredDocument("key.pdf", "file.pdf"));

        public Task UpdateProgressAsync(ProcessingJob job, string stage, int clauseCount, CancellationToken cancellationToken)
        {
            Progress.Add($"{stage}:{clauseCount}");
            return Task.CompletedTask;
        }

        public Task SaveClausesAsync(ProcessingJob job, IReadOnlyList<MergedClause> clauses, CancellationToken cancellationToken)
        {
            Saved.AddRange(clauses);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredClause>> LoadClausesAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredClause>>(Clauses);

        public Task<IReadOnlyList<ClauseCategoryInfo>> LoadActiveCategoriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClauseCategoryInfo>>(Categories);

        public Task SaveClassificationsAsync(
            ProcessingJob job,
            IReadOnlyList<ClassificationAssignment> assignments,
            int unclassifiedCount,
            CancellationToken cancellationToken)
        {
            Classifications.AddRange(assignments);
            Unclassified = unclassifiedCount;
            return Task.CompletedTask;
        }

        public Task RecordInvocationAsync(
            ProcessingJob job,
            string stage,
            int invocationNo,
            AiCompletionResult result,
            int pages,
            AiPricing pricing,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
