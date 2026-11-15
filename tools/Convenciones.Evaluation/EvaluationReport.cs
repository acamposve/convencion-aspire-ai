using System.Globalization;
using System.Text;

namespace Convenciones.Evaluation;

/// <summary>Machine-readable JSON plus a human-readable Markdown rendering of the same result.</summary>
public static class EvaluationReport
{
    public static void WriteJson(string path, EvaluationResult result) => EvaluationJson.Save(path, result);

    public static void WriteMarkdown(string path, EvaluationResult result)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToMarkdown(result) + Environment.NewLine);
    }

    public static string ToMarkdown(EvaluationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder();

        text.AppendLine("# F-06 evaluation report");
        text.AppendLine();
        text.AppendLine("| Version | Value |");
        text.AppendLine("|---|---|");
        text.AppendLine($"| Dataset | `{result.DatasetVersion}` ({result.CorpusId}) |");
        text.AppendLine($"| Prompt | `{result.PromptVersion}` |");
        text.AppendLine($"| Taxonomy | `{result.TaxonomyVersion}` |");
        text.AppendLine($"| Predictions | `{result.PredictionRunId}` / `{result.PredictionModel}` |");
        if (!string.IsNullOrWhiteSpace(result.ClassificationPromptVersion))
        {
            text.AppendLine($"| Classification prompt | `{result.ClassificationPromptVersion}` |");
        }

        if (!string.IsNullOrWhiteSpace(result.PredictionCommit))
        {
            text.AppendLine($"| Deployed commit | `{result.PredictionCommit}` |");
        }

        text.AppendLine($"| Generated (UTC) | {result.GeneratedAtUtc:yyyy-MM-ddTHH:mm:ssZ} |");
        text.AppendLine();

        text.AppendLine("## Extraction");
        text.AppendLine();
        text.AppendLine("| Metric | Value |");
        text.AppendLine("|---|---:|");
        text.AppendLine($"| Annotated clauses | {result.Extraction.Annotated} |");
        text.AppendLine($"| Predicted clauses | {result.Extraction.Predicted} |");
        text.AppendLine($"| Matched | {result.Extraction.Matched} |");
        text.AppendLine($"| Missing | {result.Extraction.Annotated - result.Extraction.Matched} |");
        text.AppendLine($"| Extra | {result.Extraction.Predicted - result.Extraction.Matched} |");
        text.AppendLine($"| Precision | {Rate(result.Extraction.Precision)} |");
        text.AppendLine($"| Recall | {Rate(result.Extraction.Recall)} |");
        text.AppendLine($"| F1 | {Rate(result.Extraction.F1)} |");
        text.AppendLine();
        text.AppendLine("Field accuracy over matched clauses:");
        text.AppendLine();
        text.AppendLine("| Field | Correct / Matched | Rate |");
        text.AppendLine("|---|---:|---:|");
        text.AppendLine($"| Number | {result.Fields.NumberCorrect} / {result.Fields.Matched} | {Rate(result.Fields.NumberRate)} |");
        text.AppendLine($"| Heading | {result.Fields.HeadingCorrect} / {result.Fields.Matched} | {Rate(result.Fields.HeadingRate)} |");
        text.AppendLine($"| Page range | {result.Fields.PageCorrect} / {result.Fields.Matched} | {Rate(result.Fields.PageRate)} |");
        text.AppendLine($"| Text fidelity | {result.Fields.TextExact} / {result.Fields.Matched} | {Rate(result.Fields.TextRate)} |");
        text.AppendLine();

        text.AppendLine("## Classification (multi-label, matched clauses only)");
        text.AppendLine();
        text.AppendLine("| Metric | Value |");
        text.AppendLine("|---|---:|");
        text.AppendLine($"| Evaluated clauses | {result.Classification.EvaluatedClauses} |");
        text.AppendLine($"| Exact set match | {result.Classification.ExactMatch} ({Rate(result.Classification.ExactMatchRate)}) |");
        text.AppendLine($"| Macro F1 (categories with support) | {Rate(result.Classification.MacroF1)} |");
        text.AppendLine($"| Micro precision / recall / F1 | {Rate(result.Classification.MicroPrecision)} / {Rate(result.Classification.MicroRecall)} / {Rate(result.Classification.MicroF1)} |");
        text.AppendLine($"| Primary accuracy | {Rate(result.Classification.PrimaryAccuracy)} over {result.Classification.PrimaryEvaluated} |");
        text.AppendLine($"| Unclassified rate | {Rate(result.Classification.UnclassifiedRate)} ({result.Classification.Unclassified} of {result.Classification.EvaluatedClauses}) |");
        text.AppendLine($"| Expected-empty clauses | {result.Classification.ExpectedEmpty} |");
        text.AppendLine($"| False-positive categories | {result.Classification.FalsePositiveCategories} |");
        text.AppendLine();

        if (result.Classification.PerCategory.Count > 0)
        {
            text.AppendLine("| Category | Support | Predicted | TP | FP | FN | P | R | F1 |");
            text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var category in result.Classification.PerCategory)
            {
                text.AppendLine(
                    $"| {category.Category} | {category.Support} | {category.Predicted} | {category.TruePositives} | " +
                    $"{category.FalsePositives} | {category.FalseNegatives} | {Rate(category.Precision)} | " +
                    $"{Rate(category.Recall)} | {Rate(category.F1)} |");
            }

            text.AppendLine();
        }

        if (result.Documents.Count > 0)
        {
            text.AppendLine("| Document | Annotated | Predicted | Matched | P | R | F1 |");
            text.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            foreach (var document in result.Documents)
            {
                text.AppendLine(
                    $"| {document.DocumentId} | {document.Annotated} | {document.Predicted} | {document.Matched} | " +
                    $"{Rate(document.Precision)} | {Rate(document.Recall)} | {Rate(document.F1)} |");
            }

            text.AppendLine();
        }

        text.AppendLine("## How to read this");
        text.AppendLine();
        text.AppendLine(
            "These numbers describe agreement with the **annotation file**, not with reality. A high score on a " +
            "small or self-authored corpus proves the scorer works; it does not prove the pipeline is accurate. " +
            "Only a representative corpus with independent, human-validated annotations supports a quality claim.");

        return text.ToString();
    }

    private static string Rate(double value) => value.ToString("F4", CultureInfo.InvariantCulture);
}
