using System.Text.Json;
using System.Text.Json.Serialization;

namespace Convenciones.Evaluation;

/// <summary>Human-authored annotation for one clause. Every field is part of the expected answer.</summary>
public sealed record AnnotatedClauseRecord(
    string ClauseId,
    string Text,
    string? Number,
    string? Heading,
    int PageFrom,
    int PageTo,
    IReadOnlyList<string> Categories,
    string? Primary,
    string Status = "draft",
    string? Annotator = null,
    string? Reviewer = null,
    string? Notes = null,
    string? Disagreement = null,
    bool Ambiguous = false);

public sealed record CorpusDocument(
    string DocumentId,
    string Title,
    IReadOnlyList<AnnotatedClauseRecord> Clauses,
    string ContractType = "unspecified");

/// <summary>
/// A versioned evaluation corpus. Versions are recorded in every report so a result can never be
/// read against a different dataset, prompt or taxonomy than the one that produced it.
/// </summary>
/// <remarks>
/// <see cref="SchemaVersion"/> 1 is the legacy demo shape (no provenance, no taxonomy list); 2 adds the
/// annotation workflow and is validated strictly. Legacy files stay loadable so historical results
/// remain reproducible.
/// </remarks>
public sealed record EvaluationDataset(
    string DatasetVersion,
    string CorpusId,
    string PromptVersion,
    string TaxonomyVersion,
    IReadOnlyList<CorpusDocument> Documents,
    int SchemaVersion = 1,
    string Status = "draft",
    IReadOnlyList<string>? TaxonomyKeys = null,
    IReadOnlyList<string>? Annotators = null,
    IReadOnlyList<string>? Reviewers = null,
    string? Notes = null);

public sealed record PredictedClauseRecord(
    string? Number,
    string? Heading,
    string Text,
    int PageFrom,
    int PageTo,
    IReadOnlyList<string> Categories,
    string? Primary);

public sealed record PredictionDocument(string DocumentId, IReadOnlyList<PredictedClauseRecord> Clauses);

/// <summary>
/// Captured model output, independent of the provider. Evaluating this file never calls Azure,
/// Document Intelligence or Mistral; it can be produced from a staging run or a recorded fixture.
/// </summary>
public sealed record PredictionSet(
    string RunId,
    string Model,
    string DatasetVersion,
    string PromptVersion,
    IReadOnlyList<PredictionDocument> Documents,
    string? Commit = null,
    string? ClassificationPromptVersion = null);

public sealed class EvaluationInputException(string message) : Exception(message);

public static class EvaluationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new EvaluationInputException($"File not found: {path}");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
                ?? throw new EvaluationInputException($"Empty JSON document: {path}");
        }
        catch (JsonException exception)
        {
            throw new EvaluationInputException($"{path} is not valid JSON: {exception.Message}");
        }
    }

    public static void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, Options) + Environment.NewLine);
    }
}

/// <summary>
/// Structural validation only: it proves the inputs can be scored, never that the annotations are
/// correct. Correctness is a property of the review protocol, not of the file format.
/// </summary>
public static class EvaluationValidator
{
    public static void ValidateDataset(EvaluationDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (dataset.Documents.Count == 0)
        {
            throw new EvaluationInputException("The dataset has no documents.");
        }

        if (string.IsNullOrWhiteSpace(dataset.DatasetVersion)
            || string.IsNullOrWhiteSpace(dataset.PromptVersion)
            || string.IsNullOrWhiteSpace(dataset.TaxonomyVersion))
        {
            throw new EvaluationInputException("Dataset, prompt and taxonomy versions are required.");
        }

        var documents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in dataset.Documents)
        {
            if (string.IsNullOrWhiteSpace(document.DocumentId) || !documents.Add(document.DocumentId))
            {
                throw new EvaluationInputException($"Document id is missing or duplicated: '{document.DocumentId}'.");
            }

            if (document.Clauses.Count == 0)
            {
                throw new EvaluationInputException($"Document '{document.DocumentId}' has no annotated clauses.");
            }

            var clauseIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var clause in document.Clauses)
            {
                if (string.IsNullOrWhiteSpace(clause.ClauseId) || !clauseIds.Add(clause.ClauseId))
                {
                    throw new EvaluationInputException(
                        $"Clause id is missing or duplicated in '{document.DocumentId}': '{clause.ClauseId}'.");
                }

                if (string.IsNullOrWhiteSpace(clause.Text))
                {
                    throw new EvaluationInputException($"Clause '{clause.ClauseId}' has empty text.");
                }

                if (clause.PageFrom < 1 || clause.PageTo < clause.PageFrom)
                {
                    throw new EvaluationInputException($"Clause '{clause.ClauseId}' has an invalid page range.");
                }

                if (clause.Categories.Any(string.IsNullOrWhiteSpace))
                {
                    throw new EvaluationInputException($"Clause '{clause.ClauseId}' has a blank category.");
                }

                if (clause.Primary is not null
                    && !clause.Categories.Contains(clause.Primary, StringComparer.OrdinalIgnoreCase))
                {
                    throw new EvaluationInputException(
                        $"Clause '{clause.ClauseId}' has a primary category outside its categories.");
                }
            }
        }

        if (dataset.SchemaVersion >= 2)
        {
            ValidateAnnotationWorkflow(dataset);
        }
    }

    /// <summary>
    /// Rules that make "draft" and "reviewed" mean something. A clause may only be reviewed by a named
    /// reviewer with no open disagreement; a corpus may only be reviewed when every clause is.
    /// </summary>
    private static void ValidateAnnotationWorkflow(EvaluationDataset dataset)
    {
        if (dataset.TaxonomyKeys is not { Count: > 0 } taxonomy)
        {
            throw new EvaluationInputException(
                $"Dataset schema {dataset.SchemaVersion} requires taxonomyKeys so categories can be validated.");
        }

        if (dataset.Annotators is not { Count: > 0 })
        {
            throw new EvaluationInputException("A corpus must name at least one annotator.");
        }

        var keys = new HashSet<string>(taxonomy, StringComparer.OrdinalIgnoreCase);
        if (keys.Count != taxonomy.Count)
        {
            throw new EvaluationInputException("taxonomyKeys contains duplicates.");
        }

        if (!IsKnownStatus(dataset.Status))
        {
            throw new EvaluationInputException($"Unknown corpus status '{dataset.Status}'.");
        }

        foreach (var document in dataset.Documents)
        {
            foreach (var clause in document.Clauses)
            {
                if (!IsKnownStatus(clause.Status))
                {
                    throw new EvaluationInputException(
                        $"Clause '{clause.ClauseId}' has unknown status '{clause.Status}'.");
                }

                if (string.IsNullOrWhiteSpace(clause.Annotator))
                {
                    throw new EvaluationInputException(
                        $"Clause '{clause.ClauseId}' has no annotator: it is a missing annotation.");
                }

                foreach (var category in clause.Categories)
                {
                    if (!keys.Contains(category))
                    {
                        throw new EvaluationInputException(
                            $"Clause '{clause.ClauseId}' uses category '{category}' that is not in taxonomyKeys.");
                    }
                }

                if (clause.Primary is not null && !keys.Contains(clause.Primary))
                {
                    throw new EvaluationInputException(
                        $"Clause '{clause.ClauseId}' has a primary category outside the taxonomy.");
                }

                if (clause.Status == "reviewed")
                {
                    if (string.IsNullOrWhiteSpace(clause.Reviewer))
                    {
                        throw new EvaluationInputException(
                            $"Clause '{clause.ClauseId}' is reviewed but names no reviewer.");
                    }

                    if (!string.IsNullOrWhiteSpace(clause.Disagreement))
                    {
                        throw new EvaluationInputException(
                            $"Clause '{clause.ClauseId}' is reviewed but still records an unresolved disagreement.");
                    }
                }

                if (dataset.Status == "reviewed" && clause.Status != "reviewed")
                {
                    throw new EvaluationInputException(
                        $"Corpus is reviewed but clause '{clause.ClauseId}' is still '{clause.Status}'.");
                }
            }
        }

        if (dataset.Status == "reviewed" && dataset.Reviewers is not { Count: > 0 })
        {
            throw new EvaluationInputException("A reviewed corpus must name its reviewers.");
        }
    }

    private static bool IsKnownStatus(string status) => status is "draft" or "reviewed";

    public static void ValidatePredictions(PredictionSet predictions)
    {
        ArgumentNullException.ThrowIfNull(predictions);

        if (string.IsNullOrWhiteSpace(predictions.RunId))
        {
            throw new EvaluationInputException("Predictions must record the run or fixture id they came from.");
        }

        var documents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in predictions.Documents)
        {
            if (string.IsNullOrWhiteSpace(document.DocumentId) || !documents.Add(document.DocumentId))
            {
                throw new EvaluationInputException($"Document id is missing or duplicated: '{document.DocumentId}'.");
            }

            foreach (var clause in document.Clauses)
            {
                if (string.IsNullOrWhiteSpace(clause.Text))
                {
                    throw new EvaluationInputException(
                        $"A predicted clause in '{document.DocumentId}' has empty text.");
                }

                if (clause.PageFrom < 1 || clause.PageTo < clause.PageFrom)
                {
                    throw new EvaluationInputException(
                        $"A predicted clause in '{document.DocumentId}' has an invalid page range.");
                }
            }
        }
    }
}
