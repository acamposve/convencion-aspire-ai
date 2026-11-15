using System.Text;

namespace Convenciones.Evaluation;

public sealed record CategoryCoverage(
    string Category,
    int Clauses,
    int Primary,
    int Documents,
    int DraftClauses = 0,
    int ReviewedClauses = 0);

public sealed record ContractTypeCoverage(string ContractType, int Documents, int Clauses);

public sealed record CorpusCoverage(
    string DatasetVersion,
    string CorpusId,
    string TaxonomyVersion,
    string Status,
    int SchemaVersion,
    int Documents,
    int Clauses,
    int DraftClauses,
    int ReviewedClauses,
    IReadOnlyList<CategoryCoverage> Categories,
    IReadOnlyList<ContractTypeCoverage> ContractTypes,
    int MultiLabelClauses,
    int UnclassifiedClauses,
    int AmbiguousClauses,
    int UnresolvedDisagreements);

/// <summary>
/// Describes what a corpus actually covers. It is deliberately unable to say whether the annotations are
/// correct: coverage is a property of the files, quality is a property of the review.
/// </summary>
public static class CorpusCoverageReport
{
    public static CorpusCoverage Build(EvaluationDataset dataset)
    {
        EvaluationValidator.ValidateDataset(dataset);

        var clauses = dataset.Documents.SelectMany(document => document.Clauses).ToList();
        var taxonomy = dataset.TaxonomyKeys is { Count: > 0 } keys
            ? keys
            : clauses.SelectMany(clause => clause.Categories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var categories = taxonomy
            .Select(key => new CategoryCoverage(
                key,
                clauses.Count(clause => clause.Categories.Contains(key, StringComparer.OrdinalIgnoreCase)),
                clauses.Count(clause => string.Equals(clause.Primary, key, StringComparison.OrdinalIgnoreCase)),
                dataset.Documents.Count(document => document.Clauses.Any(
                    clause => clause.Categories.Contains(key, StringComparer.OrdinalIgnoreCase))),
                clauses.Count(clause => clause.Categories.Contains(key, StringComparer.OrdinalIgnoreCase)
                    && clause.Status == "draft"),
                clauses.Count(clause => clause.Categories.Contains(key, StringComparer.OrdinalIgnoreCase)
                    && clause.Status == "reviewed")))
            .ToList();

        var contractTypes = dataset.Documents
            .GroupBy(document => document.ContractType, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ContractTypeCoverage(
                group.Key,
                group.Count(),
                group.Sum(document => document.Clauses.Count)))
            .OrderByDescending(entry => entry.Documents)
            .ThenBy(entry => entry.ContractType, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CorpusCoverage(
            dataset.DatasetVersion,
            dataset.CorpusId,
            dataset.TaxonomyVersion,
            dataset.Status,
            dataset.SchemaVersion,
            dataset.Documents.Count,
            clauses.Count,
            clauses.Count(clause => clause.Status == "draft"),
            clauses.Count(clause => clause.Status == "reviewed"),
            categories,
            contractTypes,
            clauses.Count(clause => clause.Categories.Count > 1),
            clauses.Count(clause => clause.Categories.Count == 0),
            clauses.Count(clause => clause.Ambiguous),
            clauses.Count(clause => !string.IsNullOrWhiteSpace(clause.Disagreement)));
    }

    public static string ToMarkdown(CorpusCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var text = new StringBuilder();

        text.AppendLine("# Corpus coverage");
        text.AppendLine();
        text.AppendLine("| Field | Value |");
        text.AppendLine("|---|---|");
        text.AppendLine($"| Corpus | `{coverage.CorpusId}` |");
        text.AppendLine($"| Dataset version | `{coverage.DatasetVersion}` (schema {coverage.SchemaVersion}) |");
        text.AppendLine($"| Taxonomy version | `{coverage.TaxonomyVersion}` |");
        text.AppendLine($"| Corpus status | **{coverage.Status}** |");
        text.AppendLine($"| Documents | {coverage.Documents} |");
        text.AppendLine($"| Clauses | {coverage.Clauses} |");
        text.AppendLine($"| Reviewed clauses | {coverage.ReviewedClauses} |");
        text.AppendLine($"| Draft clauses | {coverage.DraftClauses} |");
        text.AppendLine($"| Multi-label clauses | {coverage.MultiLabelClauses} |");
        text.AppendLine($"| Unclassified clauses | {coverage.UnclassifiedClauses} |");
        text.AppendLine($"| Ambiguous clauses | {coverage.AmbiguousClauses} |");
        text.AppendLine($"| Unresolved disagreements | {coverage.UnresolvedDisagreements} |");
        text.AppendLine();

        text.AppendLine("## Category coverage");
        text.AppendLine();
        text.AppendLine("| Category | Clauses | Primary | Documents | Draft | Reviewed | Status |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach (var category in coverage.Categories)
        {
            var status = category.Clauses == 0
                ? "**UNCOVERED**"
                : category.ReviewedClauses == 0
                    ? "draft only"
                    : category.Primary == 0 ? "no primary" : "reviewed";
            text.AppendLine(
                $"| {category.Category} | {category.Clauses} | {category.Primary} | {category.Documents} | " +
                $"{category.DraftClauses} | {category.ReviewedClauses} | {status} |");
        }

        var uncovered = coverage.Categories.Where(category => category.Clauses == 0).Select(category => category.Category).ToList();
        text.AppendLine();
        text.AppendLine(
            uncovered.Count == 0
                ? "Every taxonomy category has at least one annotated clause."
                : $"**Uncovered categories:** {string.Join(", ", uncovered)}.");
        text.AppendLine();

        if (coverage.ContractTypes.Count > 0)
        {
            text.AppendLine("## Contract types");
            text.AppendLine();
            text.AppendLine("| Type | Documents | Clauses |");
            text.AppendLine("|---|---:|---:|");
            foreach (var type in coverage.ContractTypes)
            {
                text.AppendLine($"| {type.ContractType} | {type.Documents} | {type.Clauses} |");
            }

            text.AppendLine();
        }

        text.AppendLine("## What this report does not say");
        text.AppendLine();
        text.AppendLine(
            "Coverage counts files. It cannot say whether an annotation is correct, whether two reviewers " +
            "agree, or whether the documents resemble real contracts. Until the corpus is reviewed, treat " +
            "every clause as a draft.");

        return text.ToString();
    }

    public static void WriteMarkdown(string path, CorpusCoverage coverage)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToMarkdown(coverage) + Environment.NewLine);
    }
}
