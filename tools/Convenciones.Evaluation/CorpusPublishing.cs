namespace Convenciones.Evaluation;

/// <summary>
/// Turns a <b>finalized</b> adjudication worksheet into the next dataset version. It refuses to invent
/// a decision: every clause must carry a human adjudicator, and every unresolved disagreement must have
/// an explicit final answer.
/// </summary>
public static class CorpusPublisher
{
    public static EvaluationDataset Materialize(
        EvaluationDataset source,
        AdjudicationWorksheet worksheet,
        string datasetVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(worksheet);

        if (string.IsNullOrWhiteSpace(datasetVersion))
        {
            throw new EvaluationInputException("A target dataset version is required.");
        }

        if (string.Equals(datasetVersion, source.DatasetVersion, StringComparison.Ordinal))
        {
            throw new EvaluationInputException(
                $"Dataset version must change: source is already '{source.DatasetVersion}'.");
        }

        var rows = worksheet.Rows.ToDictionary(row => row.ClauseId, StringComparer.Ordinal);
        var problems = new List<string>();
        var resolved = new Dictionary<string, AnnotatedClauseRecord>(StringComparer.Ordinal);
        var reviewers = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var document in source.Documents)
        {
            foreach (var clause in document.Clauses)
            {
                if (!rows.TryGetValue(clause.ClauseId, out var row))
                {
                    problems.Add($"Clause '{clause.ClauseId}' has no adjudication row.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.Adjudicator))
                {
                    problems.Add($"Clause '{clause.ClauseId}' has no adjudicator: a review cannot be accepted anonymously.");
                    continue;
                }

                var categories = ResolveCategories(row, out var categoryError);
                if (categoryError is not null)
                {
                    problems.Add($"Clause '{clause.ClauseId}': {categoryError}");
                    continue;
                }

                var primary = row.FinalPrimary ?? (row.Agreement == "agree" ? row.ReviewerAPrimary : null);
                if (primary is not null && !categories!.Contains(primary, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"Clause '{clause.ClauseId}': primary '{primary}' is not among the final categories.");
                    continue;
                }

                var (pageFrom, pageTo) = ResolvePages(row, clause, out var pageError);
                if (pageError is not null)
                {
                    problems.Add($"Clause '{clause.ClauseId}': {pageError}");
                    continue;
                }

                reviewers.Add(row.Adjudicator);

                resolved[clause.ClauseId] = clause with
                {
                    Categories = categories!,
                    Primary = primary,
                    PageFrom = pageFrom,
                    PageTo = pageTo,
                    Status = "reviewed",
                    Annotator = $"{worksheet.ReviewerAIdentity} + {worksheet.ReviewerBIdentity}",
                    Reviewer = row.Adjudicator,
                    Ambiguous = row.ReviewerAAmbiguous || row.ReviewerBAmbiguous,
                    Notes = string.IsNullOrWhiteSpace(row.Rationale) ? clause.Notes : row.Rationale,
                    Disagreement = null,
                };
            }
        }

        foreach (var key in rows.Keys.Where(key => source.Documents
                     .SelectMany(document => document.Clauses)
                     .All(clause => clause.ClauseId != key)))
        {
            problems.Add($"Adjudication row '{key}' does not belong to the corpus.");
        }

        if (problems.Count > 0)
        {
            throw new EvaluationInputException(
                "The adjudication cannot be materialized:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems));
        }

        var documents = source.Documents
            .Select(document => document with
            {
                Clauses = document.Clauses.Select(clause => resolved[clause.ClauseId]).ToList(),
            })
            .ToList();

        var result = source with
        {
            DatasetVersion = datasetVersion,
            SchemaVersion = 2,
            Status = "reviewed",
            Annotators = [worksheet.ReviewerAIdentity, worksheet.ReviewerBIdentity],
            Reviewers = reviewers.ToList(),
            Documents = documents,
        };

        // The published corpus must satisfy the same rules as any hand-written one.
        EvaluationValidator.ValidateDataset(result);
        return result;
    }

    private static IReadOnlyList<string>? ResolveCategories(AdjudicationRow row, out string? error)
    {
        error = null;

        if (row.FinalCategories is { } final)
        {
            if (final.Count > 0 && string.IsNullOrWhiteSpace(row.FinalPrimary))
            {
                error = "finalCategories was set but finalPrimary is missing.";
                return null;
            }

            return final;
        }

        // Page disagreements also mark the row "disagree"; categories must be judged on their own.
        var categoriesAgree = new HashSet<string>(row.ReviewerACategories, StringComparer.OrdinalIgnoreCase)
            .SetEquals(new HashSet<string>(row.ReviewerBCategories, StringComparer.OrdinalIgnoreCase));
        var primaryAgrees = string.Equals(row.ReviewerAPrimary, row.ReviewerBPrimary, StringComparison.OrdinalIgnoreCase);

        if (!categoriesAgree || !primaryAgrees)
        {
            error = "reviewers disagree and no finalCategories decision was recorded.";
            return null;
        }

        return row.ReviewerACategories;
    }

    private static (int PageFrom, int PageTo) ResolvePages(
        AdjudicationRow row,
        AnnotatedClauseRecord clause,
        out string? error)
    {
        error = null;

        if (row.FinalPageFrom is { } finalFrom && row.FinalPageTo is { } finalTo)
        {
            if (finalFrom < 1 || finalTo < finalFrom)
            {
                error = $"final page range {finalFrom}-{finalTo} is invalid.";
                return (clause.PageFrom, clause.PageTo);
            }

            return (finalFrom, finalTo);
        }

        if (row.ReviewerAPageFrom > 0 && row.ReviewerAPageFrom == row.ReviewerBPageFrom
            && row.ReviewerAPageTo == row.ReviewerBPageTo && row.ReviewerAPageTo >= row.ReviewerAPageFrom)
        {
            return (row.ReviewerAPageFrom, row.ReviewerAPageTo);
        }

        error = "reviewers reported different page ranges and no final page range was recorded.";
        return (clause.PageFrom, clause.PageTo);
    }
}
