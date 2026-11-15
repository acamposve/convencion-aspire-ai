using System.Text;

namespace Convenciones.Evaluation;

/// <summary>One clause as a reviewer sees it. Answers are empty in an export and filled in a submission.</summary>
public sealed record ReviewClause(
    string ClauseId,
    string DocumentId,
    string ContractType,
    string? Number,
    string? Heading,
    string Text,
    int PageFrom,
    int PageTo,
    IReadOnlyList<string> Categories,
    string? Primary,
    bool Ambiguous,
    string? Notes);

/// <summary>
/// A blind review package. It deliberately carries no draft categories: showing them would anchor the
/// reviewer on the very answer the round exists to test.
/// </summary>
public sealed record ReviewPackage(
    string PackageId,
    string CorpusId,
    string DatasetVersion,
    string TaxonomyVersion,
    string ReviewerSlot,
    string? ReviewerIdentity,
    string Status,
    IReadOnlyList<string> TaxonomyKeys,
    string Instructions,
    IReadOnlyList<ReviewClause> Clauses,
    string SubmissionVersion = "1");

public sealed record AdjudicationRow(
    string ClauseId,
    string DocumentId,
    string Text,
    IReadOnlyList<string> DraftCategories,
    string? DraftPrimary,
    IReadOnlyList<string> ReviewerACategories,
    string? ReviewerAPrimary,
    IReadOnlyList<string> ReviewerBCategories,
    string? ReviewerBPrimary,
    string Agreement,
    string? DisagreementType,
    string? Resolution,
    string? Rationale,
    string? Adjudicator,
    IReadOnlyList<string>? FinalCategories,
    string? FinalPrimary);

public sealed record AdjudicationWorksheet(
    string PackageId,
    string CorpusId,
    string DatasetVersion,
    string ReviewerAIdentity,
    string ReviewerBIdentity,
    IReadOnlyList<AdjudicationRow> Rows);

/// <summary>Builds, validates and compares review packages. It never resolves a disagreement itself.</summary>
public static class ReviewWorkflow
{
    public const string InstructionText =
        "Read each clause against its document. Select every taxonomy key that genuinely applies " +
        "(zero, one or several), then mark the dominant one as primary. Leave the list empty when no " +
        "category fits. Set ambiguous=true when a careful reviewer could reasonably choose differently, " +
        "and explain why in notes. Do not consult the other reviewer. Do not use an AI assistant to " +
        "choose categories: the round exists to measure independent human judgement.";

    public static ReviewPackage ForReviewer(EvaluationDataset dataset, string slot)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        EvaluationValidator.ValidateDataset(dataset);

        if (slot is not ("A" or "B"))
        {
            throw new EvaluationInputException($"Reviewer slot must be 'A' or 'B', not '{slot}'.");
        }

        var clauses = dataset.Documents
            .SelectMany(document => document.Clauses.Select(clause => new ReviewClause(
                clause.ClauseId,
                document.DocumentId,
                document.ContractType,
                clause.Number,
                clause.Heading,
                clause.Text,
                clause.PageFrom,
                clause.PageTo,
                [],
                null,
                false,
                null)))
            .ToList();

        return new ReviewPackage(
            $"{dataset.CorpusId}-{dataset.DatasetVersion}-review-{slot}",
            dataset.CorpusId,
            dataset.DatasetVersion,
            dataset.TaxonomyVersion,
            slot,
            null,
            "blank",
            dataset.TaxonomyKeys ?? [],
            InstructionText,
            clauses);
    }

    /// <summary>Returns the problems that make a submission unusable. Empty means it can be imported.</summary>
    public static IReadOnlyList<string> ValidateSubmission(EvaluationDataset dataset, ReviewPackage submission)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(submission);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(submission.ReviewerIdentity))
        {
            problems.Add("reviewerIdentity is required: an anonymous review cannot be adjudicated.");
        }

        if (!string.Equals(submission.CorpusId, dataset.CorpusId, StringComparison.Ordinal)
            || !string.Equals(submission.DatasetVersion, dataset.DatasetVersion, StringComparison.Ordinal))
        {
            problems.Add(
                $"Submission targets {submission.CorpusId} v{submission.DatasetVersion}, but the corpus is " +
                $"{dataset.CorpusId} v{dataset.DatasetVersion}.");
        }

        if (submission.ReviewerSlot is not ("A" or "B"))
        {
            problems.Add($"Unknown reviewer slot '{submission.ReviewerSlot}'.");
        }

        var taxonomy = new HashSet<string>(dataset.TaxonomyKeys ?? [], StringComparer.OrdinalIgnoreCase);
        var expected = dataset.Documents.SelectMany(document => document.Clauses)
            .ToDictionary(clause => clause.ClauseId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var answer in submission.Clauses)
        {
            if (!seen.Add(answer.ClauseId))
            {
                problems.Add($"Duplicate clause id '{answer.ClauseId}' in the submission.");
                continue;
            }

            if (!expected.ContainsKey(answer.ClauseId))
            {
                problems.Add($"Unknown clause id '{answer.ClauseId}'.");
                continue;
            }

            if (taxonomy.Count > 0 && answer.Categories.Any(category => !taxonomy.Contains(category)))
            {
                var invalid = answer.Categories.First(category => !taxonomy.Contains(category));
                problems.Add($"Clause '{answer.ClauseId}' uses unknown category '{invalid}'.");
            }

            if (answer.Primary is not null
                && !answer.Categories.Contains(answer.Primary, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"Clause '{answer.ClauseId}' has a primary category outside its selection.");
            }
        }

        foreach (var missing in expected.Keys.Where(id => !seen.Contains(id)))
        {
            problems.Add($"Clause '{missing}' has no answer.");
        }

        return problems;
    }

    public static void Import(EvaluationDataset dataset, ReviewPackage submission, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            throw new EvaluationInputException(
                $"Refusing to overwrite an existing import: {outputPath}. Use a new package id instead.");
        }

        var problems = ValidateSubmission(dataset, submission);
        if (problems.Count > 0)
        {
            throw new EvaluationInputException(
                "The submission cannot be imported:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        EvaluationJson.Save(outputPath, submission with { Status = "submitted" });
    }

    public static AdjudicationWorksheet Adjudicate(
        EvaluationDataset dataset,
        ReviewPackage reviewerA,
        ReviewPackage reviewerB)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(reviewerA);
        ArgumentNullException.ThrowIfNull(reviewerB);

        foreach (var (package, slot) in new[] { (reviewerA, "A"), (reviewerB, "B") })
        {
            if (package.ReviewerSlot != slot)
            {
                throw new EvaluationInputException($"Expected a slot {slot} package, got '{package.ReviewerSlot}'.");
            }

            var problems = ValidateSubmission(dataset, package);
            if (problems.Count > 0)
            {
                throw new EvaluationInputException(
                    $"Reviewer {slot} submission is incomplete:" + Environment.NewLine +
                    string.Join(Environment.NewLine, problems));
            }
        }

        var answersA = reviewerA.Clauses.ToDictionary(clause => clause.ClauseId, StringComparer.Ordinal);
        var answersB = reviewerB.Clauses.ToDictionary(clause => clause.ClauseId, StringComparer.Ordinal);

        var rows = dataset.Documents
            .SelectMany(document => document.Clauses.Select(clause =>
            {
                var a = answersA[clause.ClauseId];
                var b = answersB[clause.ClauseId];
                var categoriesAgree = SetEquals(a.Categories, b.Categories);
                var primaryAgree = string.Equals(a.Primary, b.Primary, StringComparison.OrdinalIgnoreCase);

                return new AdjudicationRow(
                    clause.ClauseId,
                    document.DocumentId,
                    clause.Text,
                    clause.Categories,
                    clause.Primary,
                    a.Categories,
                    a.Primary,
                    b.Categories,
                    b.Primary,
                    categoriesAgree && primaryAgree ? "agree" : "disagree",
                    categoriesAgree && primaryAgree
                        ? null
                        : categoriesAgree ? "primary" : primaryAgree ? "categories" : "categories+primary",
                    null,
                    null,
                    null,
                    null,
                    null);
            }))
            .ToList();

        return new AdjudicationWorksheet(
            $"{dataset.CorpusId}-{dataset.DatasetVersion}-adjudication",
            dataset.CorpusId,
            dataset.DatasetVersion,
            reviewerA.ReviewerIdentity!,
            reviewerB.ReviewerIdentity!,
            rows);
    }

    private static bool SetEquals(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        new HashSet<string>(left, StringComparer.OrdinalIgnoreCase)
            .SetEquals(new HashSet<string>(right, StringComparer.OrdinalIgnoreCase));

    public static string ToMarkdown(ReviewPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var text = new StringBuilder();

        text.AppendLine($"# Review package — reviewer {package.ReviewerSlot}");
        text.AppendLine();
        text.AppendLine($"**Package:** `{package.PackageId}` · **Corpus:** `{package.CorpusId}` " +
            $"v`{package.DatasetVersion}` · **Taxonomy:** `{package.TaxonomyVersion}` · " +
            $"**Status:** {package.Status}");
        text.AppendLine();
        text.AppendLine("## How to review");
        text.AppendLine();
        text.AppendLine(package.Instructions);
        text.AppendLine();
        text.AppendLine("Allowed categories: " + string.Join(", ", package.TaxonomyKeys.Select(key => $"`{key}`")));
        text.AppendLine();
        text.AppendLine("Record your answers in the JSON file beside this one. Replace `[]` with the categories " +
            "you select, set `primary` to one of them (or `null`), and set `reviewerIdentity` at the top.");
        text.AppendLine();
        text.AppendLine("> This is a **blind** copy: the draft categories are intentionally not shown.");
        text.AppendLine();

        foreach (var group in package.Clauses.GroupBy(clause => clause.DocumentId))
        {
            text.AppendLine($"## {group.Key}");
            text.AppendLine();
            foreach (var clause in group)
            {
                text.AppendLine($"### {clause.ClauseId} — {clause.Number ?? "(no number)"} / {clause.Heading ?? "(no heading)"}");
                text.AppendLine();
                text.AppendLine($"Pages {clause.PageFrom}–{clause.PageTo} · type {clause.ContractType}");
                text.AppendLine();
                text.AppendLine($"> {clause.Text}");
                text.AppendLine();
                text.AppendLine("- categories: ");
                text.AppendLine("- primary: ");
                text.AppendLine("- ambiguous: ");
                text.AppendLine("- notes: ");
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    public static string ToMarkdown(AdjudicationWorksheet worksheet)
    {
        ArgumentNullException.ThrowIfNull(worksheet);
        var text = new StringBuilder();

        text.AppendLine("# Adjudication worksheet");
        text.AppendLine();
        text.AppendLine($"**Corpus:** `{worksheet.CorpusId}` v`{worksheet.DatasetVersion}`");
        text.AppendLine($"**Reviewer A:** `{worksheet.ReviewerAIdentity}` · **Reviewer B:** `{worksheet.ReviewerBIdentity}`");
        text.AppendLine();
        text.AppendLine(
            "No decision is made here. Fill `resolution`, `rationale`, `adjudicator` and `finalCategories` " +
            "by hand for every row marked **disagree**. A row only becomes reviewed ground truth through " +
            "that documented decision.");
        text.AppendLine();
        text.AppendLine("| Clause | Draft | A | B | Agreement |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var row in worksheet.Rows)
        {
            text.AppendLine(
                $"| {row.ClauseId} | {Format(row.DraftCategories)} | {Format(row.ReviewerACategories)} | " +
                $"{Format(row.ReviewerBCategories)} | {row.Agreement}{(row.DisagreementType is null ? "" : $" ({row.DisagreementType})")} |");
        }

        text.AppendLine();
        var disagreements = worksheet.Rows.Count(row => row.Agreement == "disagree");
        text.AppendLine($"**{worksheet.Rows.Count} rows, {disagreements} disagreements, " +
            $"{worksheet.Rows.Count - disagreements} agreements.**");

        return text.ToString();
    }

    private static string Format(IReadOnlyList<string> categories) =>
        categories.Count == 0 ? "—" : string.Join(", ", categories);
}
