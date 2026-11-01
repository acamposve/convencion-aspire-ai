using System.Text.Json;
using Convenciones.Ai;
using Convenciones.Worker;
using Microsoft.Extensions.Logging;

namespace Convenciones.Worker.Extraction;

/// <summary>
/// Classifies every extracted clause in batches, rejecting any category key outside the active
/// taxonomy. Zero categories is a valid answer and is counted as Unclassified.
/// </summary>
public sealed class ClauseClassificationStage(
    IAiModelClient modelClient,
    IClauseStore store,
    ClassificationOptions options,
    ILogger<ClauseClassificationStage> logger) : IProcessingStage
{
    public async Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        var clauses = await store.LoadClausesAsync(job.RunId, cancellationToken).ConfigureAwait(false);
        if (clauses.Count == 0)
        {
            throw new PermanentProcessingException("no_clauses_found", "There are no clauses to classify.");
        }

        var categories = await store.LoadActiveCategoriesAsync(cancellationToken).ConfigureAwait(false);
        if (categories.Count == 0)
        {
            throw new PermanentProcessingException("taxonomy_empty", "The clause taxonomy has no active categories.");
        }

        var byKey = categories.ToDictionary(category => category.Key, StringComparer.OrdinalIgnoreCase);
        var byOrdinal = clauses.ToDictionary(clause => clause.Ordinal);
        var assignments = new List<ClassificationAssignment>();
        long inputTokens = 0;
        long outputTokens = 0;
        var classifiedOrdinals = new HashSet<int>();

        foreach (var batch in Batches(clauses, options.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            AiCompletionResult result;
            try
            {
                result = await modelClient.CompleteAsync(
                    new AiCompletionRequest(
                        ClassificationPrompt.System,
                        ClassificationPrompt.User(categories, batch),
                        ClassificationSchema.Json,
                        options.MaxOutputTokens,
                        options.Deployment),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AiInvalidResponseException exception)
            {
                throw new PermanentProcessingException("invalid_model_output", exception.Message);
            }

            Parse(result.Content, batch, byOrdinal, byKey, assignments, classifiedOrdinals);
            inputTokens += result.InputTokens ?? 0;
            outputTokens += result.OutputTokens ?? 0;
        }

        await store.SaveClassificationsAsync(
            job,
            assignments,
            clauses.Count - classifiedOrdinals.Count,
            cancellationToken).ConfigureAwait(false);

        var cost = (decimal)((inputTokens / 1_000_000.0 * options.InputPricePerMillion)
            + (outputTokens / 1_000_000.0 * options.OutputPricePerMillion));
        await store.RecordUsageAsync(job.CompanyId, 0, inputTokens, outputTokens, cost, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Classified {Classified} of {Total} clause(s) in run {RunId}.",
            classifiedOrdinals.Count,
            clauses.Count,
            job.RunId);
    }

    private static void Parse(
        JsonElement content,
        IReadOnlyList<StoredClause> batch,
        IReadOnlyDictionary<int, StoredClause> byOrdinal,
        IReadOnlyDictionary<string, ClauseCategoryInfo> byKey,
        List<ClassificationAssignment> assignments,
        HashSet<int> classifiedOrdinals)
    {
        if (!content.TryGetProperty("classifications", out var classifications)
            || classifications.ValueKind is not JsonValueKind.Array)
        {
            throw new PermanentProcessingException("invalid_model_output", "The response has no classifications array.");
        }

        var batchOrdinals = batch.Select(clause => clause.Ordinal).ToHashSet();

        foreach (var item in classifications.EnumerateArray())
        {
            var ordinal = item.TryGetProperty("clause_ordinal", out var ordinalElement)
                && ordinalElement.TryGetInt32(out var value)
                ? value
                : 0;

            if (!batchOrdinals.Contains(ordinal) || !byOrdinal.ContainsKey(ordinal))
            {
                throw new PermanentProcessingException("invalid_model_output", "A classification references an unknown clause.");
            }

            if (!item.TryGetProperty("categories", out var categoryArray)
                || categoryArray.ValueKind is not JsonValueKind.Array)
            {
                throw new PermanentProcessingException("invalid_model_output", "A classification has no categories array.");
            }

            var clause = byOrdinal[ordinal];
            var primaryKey = item.TryGetProperty("primary", out var primaryElement) && primaryElement.ValueKind is JsonValueKind.String
                ? primaryElement.GetString()
                : null;
            var rationale = item.TryGetProperty("rationale", out var rationaleElement) && rationaleElement.ValueKind is JsonValueKind.String
                ? rationaleElement.GetString()
                : null;

            if (rationale is { Length: > 240 })
            {
                rationale = rationale[..240];
            }

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var perClause = new List<(string Key, double Confidence)>();

            foreach (var category in categoryArray.EnumerateArray())
            {
                var key = category.TryGetProperty("key", out var keyElement) ? keyElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(key) || !byKey.TryGetValue(key, out var definition))
                {
                    throw new PermanentProcessingException(
                        "invalid_model_output",
                        $"The model returned an unknown category key '{key}'.");
                }

                var confidence = category.TryGetProperty("confidence", out var confidenceElement)
                    && confidenceElement.TryGetDouble(out var confidenceValue)
                    ? confidenceValue
                    : 0;

                if (confidence is < 0 or > 1)
                {
                    throw new PermanentProcessingException("invalid_model_output", "A classification confidence is outside 0..1.");
                }

                if (seenKeys.Add(definition.Key))
                {
                    perClause.Add((definition.Key, confidence));
                }
            }

            if (perClause.Count == 0)
            {
                continue;
            }

            classifiedOrdinals.Add(ordinal);
            var primary = primaryKey is not null && seenKeys.Contains(primaryKey)
                ? primaryKey
                : perClause.MaxBy(item => item.Confidence).Key;

            foreach (var (key, confidence) in perClause)
            {
                var definition = byKey[key];
                assignments.Add(new ClassificationAssignment(
                    clause.Id,
                    definition.Id,
                    confidence,
                    string.Equals(key, primary, StringComparison.OrdinalIgnoreCase),
                    rationale));
            }
        }
    }

    private static IEnumerable<IReadOnlyList<StoredClause>> Batches(
        IReadOnlyList<StoredClause> clauses,
        int batchSize)
    {
        for (var index = 0; index < clauses.Count; index += batchSize)
        {
            yield return clauses.Skip(index).Take(batchSize).ToArray();
        }
    }
}
