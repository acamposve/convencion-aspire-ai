using System.Text.Json;
using Convenciones.Ai;
using Convenciones.Worker;
using Convenciones.Worker.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Extraction;

public sealed class ClassificationPipelineTests
{
    private static readonly ClauseCategoryInfo Pricing =
        new(Guid.NewGuid(), "pricing", "Precio y pago", "Salarios y aumentos");

    private static readonly ClauseCategoryInfo Termination =
        new(Guid.NewGuid(), "termination", "Terminación", "Causas de terminación");

    [Fact]
    public async Task Multi_label_is_persisted_and_unclassified_is_counted()
    {
        var store = StoreWithClauses(2);
        store.Categories.AddRange([Pricing, Termination]);
        var model = new FakeClassificationModel(_ => """
            {"classifications":[
              {"clause_ordinal":1,"categories":[{"key":"pricing","confidence":0.9},{"key":"termination","confidence":0.6}],"primary":"pricing","rationale":"Aumento salarial"},
              {"clause_ordinal":2,"categories":[]}
            ]}
            """);
        var stage = CreateStage(model, store);

        await stage.ExecuteAsync(Job(), CancellationToken.None);

        store.Classifications.Count.ShouldBe(2);
        store.Classifications.Count(item => item.IsPrimary).ShouldBe(1);
        store.Unclassified.ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_category_key_is_rejected()
    {
        var store = StoreWithClauses(1);
        store.Categories.Add(Pricing);
        var model = new FakeClassificationModel(_ => """
            {"classifications":[{"clause_ordinal":1,"categories":[{"key":"invented","confidence":0.9}]}]}
            """);
        var stage = CreateStage(model, store);

        var exception = await Should.ThrowAsync<PermanentProcessingException>(
            () => stage.ExecuteAsync(Job(), CancellationToken.None));
        exception.Code.ShouldBe("invalid_model_output");
        store.Classifications.ShouldBeEmpty();
    }

    [Fact]
    public async Task Clauses_are_classified_in_batches_of_twenty()
    {
        var store = StoreWithClauses(25);
        store.Categories.Add(Pricing);
        var model = new FakeClassificationModel(_ => """{"classifications":[]}""");
        var stage = CreateStage(model, store);

        await stage.ExecuteAsync(Job(), CancellationToken.None);

        model.Calls.ShouldBe(2);
    }

    [Fact]
    public void Prompt_lists_only_allowed_keys()
    {
        var prompt = ClassificationPrompt.User([Pricing], [new StoredClause(Guid.NewGuid(), 1, null, null, "Texto")]);

        prompt.ShouldContain("pricing");
        prompt.ShouldContain("Precio y pago");
        prompt.ShouldNotContain("invented");
    }

    private static ClauseClassificationStage CreateStage(FakeClassificationModel model, FakeClassificationStore store) =>
        new(
            model,
            store,
            new ClassificationOptions { BatchSize = 20 },
            NullLogger<ClauseClassificationStage>.Instance);

    private static ProcessingJob Job() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);

    private static FakeClassificationStore StoreWithClauses(int count)
    {
        var store = new FakeClassificationStore();
        for (var ordinal = 1; ordinal <= count; ordinal++)
        {
            store.Clauses.Add(new StoredClause(Guid.NewGuid(), ordinal, null, null, $"Cláusula {ordinal}."));
        }

        return store;
    }

    private sealed class FakeClassificationModel(Func<AiCompletionRequest, string> response) : IAiModelClient
    {
        public int Calls { get; private set; }

        public Task<AiCompletionResult> CompleteAsync(AiCompletionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            using var document = JsonDocument.Parse(response(request));
            return Task.FromResult(new AiCompletionResult(
                document.RootElement.Clone(),
                "fake",
                "clauses",
                10,
                5,
                TimeSpan.Zero));
        }
    }

    private sealed class FakeClassificationStore : IClauseStore
    {
        public List<StoredClause> Clauses { get; } = [];

        public List<ClauseCategoryInfo> Categories { get; } = [];

        public List<ClassificationAssignment> Classifications { get; } = [];

        public int Unclassified { get; private set; }

        public Task<StoredDocument?> LoadDocumentAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult<StoredDocument?>(new StoredDocument("key.pdf", "file.pdf"));

        public Task UpdateProgressAsync(Guid runId, string stage, int clauseCount, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SaveClausesAsync(ProcessingJob job, IReadOnlyList<MergedClause> clauses, CancellationToken cancellationToken) =>
            Task.CompletedTask;

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

        public Task RecordUsageAsync(
            Guid companyId,
            int pages,
            long inputTokens,
            long outputTokens,
            decimal costEstimateUsd,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
