namespace Convenciones.Evaluation;

public sealed record DocumentExtractionResult(
    string DocumentId,
    int Annotated,
    int Predicted,
    int Matched,
    int Missing,
    int Extra,
    double Precision,
    double Recall,
    double F1,
    int NumberCorrect,
    int HeadingCorrect,
    int PageCorrect,
    int TextExact);

public sealed record FieldAccuracy(
    int Matched,
    int NumberCorrect,
    int HeadingCorrect,
    int PageCorrect,
    int TextExact)
{
    public double NumberRate => Rate(NumberCorrect);

    public double HeadingRate => Rate(HeadingCorrect);

    public double PageRate => Rate(PageCorrect);

    public double TextRate => Rate(TextExact);

    private double Rate(int correct) => Matched == 0 ? 0 : Math.Round((double)correct / Matched, 4);
}

public sealed record CategoryScore(
    string Category,
    int Support,
    int Predicted,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    double Precision,
    double Recall,
    double F1);

/// <summary>
/// Multi-label classification metrics over clause pairs that were matched by boundary. Segmentation
/// errors are therefore scored once, by the extraction metrics, and never double-counted here.
/// </summary>
public sealed record QualityClassificationResult(
    int EvaluatedClauses,
    int ExactMatch,
    double ExactMatchRate,
    double MacroF1,
    double MicroPrecision,
    double MicroRecall,
    double MicroF1,
    int PrimaryEvaluated,
    double PrimaryAccuracy,
    int Unclassified,
    double UnclassifiedRate,
    int ExpectedEmpty,
    int FalsePositiveCategories,
    IReadOnlyList<CategoryScore> PerCategory);

public sealed record EvaluationResult(
    string DatasetVersion,
    string CorpusId,
    string PromptVersion,
    string TaxonomyVersion,
    string PredictionRunId,
    string PredictionModel,
    string? PredictionCommit,
    string? ClassificationPromptVersion,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<DocumentExtractionResult> Documents,
    BoundaryResult Extraction,
    FieldAccuracy Fields,
    QualityClassificationResult Classification);

/// <summary>
/// Deterministic scoring of captured predictions against a versioned corpus. It has no network,
/// database or provider dependency: the only inputs are the two JSON files it is given.
/// </summary>
public static class EvaluationEngine
{
    public static EvaluationResult Evaluate(EvaluationDataset dataset, PredictionSet predictions)
    {
        EvaluationValidator.ValidateDataset(dataset);
        EvaluationValidator.ValidatePredictions(predictions);

        if (!string.Equals(dataset.DatasetVersion, predictions.DatasetVersion, StringComparison.Ordinal))
        {
            throw new EvaluationInputException(
                $"Predictions were produced for dataset '{predictions.DatasetVersion}' but the corpus is " +
                $"'{dataset.DatasetVersion}'. Refusing to score mismatched versions.");
        }

        var predictedByDocument = predictions.Documents.ToDictionary(
            document => document.DocumentId,
            document => document.Clauses,
            StringComparer.Ordinal);

        var perDocument = new List<DocumentExtractionResult>();
        var annotatedTotal = 0;
        var predictedTotal = 0;
        var matchedTotal = 0;
        var numberCorrect = 0;
        var headingCorrect = 0;
        var pageCorrect = 0;
        var textExact = 0;

        var classification = new ClassificationAccumulator();

        foreach (var document in dataset.Documents)
        {
            var predictedClauses = predictedByDocument.TryGetValue(document.DocumentId, out var clauses)
                ? clauses
                : [];

            var annotated = document.Clauses
                .Select(clause => new AnnotatedClause(clause.Text, clause.PageFrom, clause.PageTo))
                .ToList();
            var predicted = predictedClauses
                .Select(clause => new PredictedClause(clause.Text, clause.PageFrom, clause.PageTo))
                .ToList();

            var matches = BoundaryMetrics.Match(annotated, predicted);
            var documentNumber = 0;
            var documentHeading = 0;
            var documentPage = 0;
            var documentText = 0;

            foreach (var match in matches)
            {
                var expected = document.Clauses[match.AnnotatedIndex];
                var actual = predictedClauses[match.PredictedIndex];

                if (LabelsEqual(expected.Number, actual.Number))
                {
                    documentNumber++;
                }

                if (LabelsEqual(expected.Heading, actual.Heading))
                {
                    documentHeading++;
                }

                if (expected.PageFrom == actual.PageFrom && expected.PageTo == actual.PageTo)
                {
                    documentPage++;
                }

                if (TextsEqual(expected.Text, actual.Text))
                {
                    documentText++;
                }

                classification.Add(expected.Categories, actual.Categories, expected.Primary, actual.Primary);
            }

            var precision = predicted.Count == 0 ? 0 : (double)matches.Count / predicted.Count;
            var recall = annotated.Count == 0 ? 0 : (double)matches.Count / annotated.Count;
            var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

            perDocument.Add(new DocumentExtractionResult(
                document.DocumentId,
                annotated.Count,
                predicted.Count,
                matches.Count,
                annotated.Count - matches.Count,
                predicted.Count - matches.Count,
                Math.Round(precision, 4),
                Math.Round(recall, 4),
                Math.Round(f1, 4),
                documentNumber,
                documentHeading,
                documentPage,
                documentText));

            annotatedTotal += annotated.Count;
            predictedTotal += predicted.Count;
            matchedTotal += matches.Count;
            numberCorrect += documentNumber;
            headingCorrect += documentHeading;
            pageCorrect += documentPage;
            textExact += documentText;
        }

        var extractionPrecision = predictedTotal == 0 ? 0 : (double)matchedTotal / predictedTotal;
        var extractionRecall = annotatedTotal == 0 ? 0 : (double)matchedTotal / annotatedTotal;
        var extractionF1 = extractionPrecision + extractionRecall == 0
            ? 0
            : 2 * extractionPrecision * extractionRecall / (extractionPrecision + extractionRecall);

        return new EvaluationResult(
            dataset.DatasetVersion,
            dataset.CorpusId,
            dataset.PromptVersion,
            dataset.TaxonomyVersion,
            predictions.RunId,
            predictions.Model,
            predictions.Commit,
            predictions.ClassificationPromptVersion,
            DateTimeOffset.UtcNow,
            perDocument,
            new BoundaryResult(
                annotatedTotal,
                predictedTotal,
                matchedTotal,
                Math.Round(extractionPrecision, 4),
                Math.Round(extractionRecall, 4),
                Math.Round(extractionF1, 4)),
            new FieldAccuracy(matchedTotal, numberCorrect, headingCorrect, pageCorrect, textExact),
            classification.Build());
    }

    internal static string NormalizeLabel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ToUpperInvariant();

    internal static string NormalizeText(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool LabelsEqual(string? expected, string? actual) =>
        string.Equals(NormalizeLabel(expected), NormalizeLabel(actual), StringComparison.Ordinal);

    private static bool TextsEqual(string expected, string actual) =>
        string.Equals(NormalizeText(expected), NormalizeText(actual), StringComparison.Ordinal);

    private sealed class ClassificationAccumulator
    {
        private readonly Dictionary<string, int[]> _counts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _support = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _predicted = new(StringComparer.OrdinalIgnoreCase);

        private int _evaluated;
        private int _exact;
        private int _primaryEvaluated;
        private int _primaryCorrect;
        private int _unclassified;
        private int _expectedEmpty;
        private int _falsePositiveCategories;

        public void Add(
            IReadOnlyList<string> expectedCategories,
            IReadOnlyList<string> predictedCategories,
            string? expectedPrimary,
            string? actualPrimary)
        {
            _evaluated++;

            var expected = new HashSet<string>(expectedCategories, StringComparer.OrdinalIgnoreCase);
            var actual = new HashSet<string>(predictedCategories, StringComparer.OrdinalIgnoreCase);

            if (expected.SetEquals(actual))
            {
                _exact++;
            }

            if (expected.Count == 0)
            {
                _expectedEmpty++;
            }

            if (actual.Count == 0)
            {
                _unclassified++;
            }

            foreach (var category in expected)
            {
                _support[category] = _support.GetValueOrDefault(category) + 1;
            }

            foreach (var category in actual)
            {
                _predicted[category] = _predicted.GetValueOrDefault(category) + 1;
            }

            foreach (var category in expected.Concat(actual).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var inExpected = expected.Contains(category);
                var inActual = actual.Contains(category);

                if (inActual && !inExpected)
                {
                    _falsePositiveCategories++;
                }

                if (!_counts.TryGetValue(category, out var counts))
                {
                    counts = new int[3];
                    _counts[category] = counts;
                }

                if (inExpected && inActual)
                {
                    counts[0]++;
                }
                else if (inActual)
                {
                    counts[1]++;
                }
                else
                {
                    counts[2]++;
                }
            }

            if (expectedPrimary is not null)
            {
                _primaryEvaluated++;
                if (string.Equals(
                        NormalizeLabel(expectedPrimary),
                        NormalizeLabel(actualPrimary),
                        StringComparison.Ordinal))
                {
                    _primaryCorrect++;
                }
            }
        }

        public QualityClassificationResult Build()
        {
            var perCategory = new List<CategoryScore>();
            var macroSum = 0.0;
            var macroCount = 0;
            var tpTotal = 0;
            var fpTotal = 0;
            var fnTotal = 0;

            foreach (var category in _counts.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
            {
                var counts = _counts[category];
                var tp = counts[0];
                var fp = counts[1];
                var fn = counts[2];
                var support = _support.GetValueOrDefault(category);
                var predicted = _predicted.GetValueOrDefault(category);

                var precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
                var recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
                var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

                perCategory.Add(new CategoryScore(
                    category,
                    support,
                    predicted,
                    tp,
                    fp,
                    fn,
                    Math.Round(precision, 4),
                    Math.Round(recall, 4),
                    Math.Round(f1, 4)));

                tpTotal += tp;
                fpTotal += fp;
                fnTotal += fn;

                // Categories with no positive examples cannot contribute a meaningful F1.
                if (support > 0)
                {
                    macroSum += f1;
                    macroCount++;
                }
            }

            var microPrecision = tpTotal + fpTotal == 0 ? 0 : (double)tpTotal / (tpTotal + fpTotal);
            var microRecall = tpTotal + fnTotal == 0 ? 0 : (double)tpTotal / (tpTotal + fnTotal);
            var microF1 = microPrecision + microRecall == 0
                ? 0
                : 2 * microPrecision * microRecall / (microPrecision + microRecall);

            return new QualityClassificationResult(
                _evaluated,
                _exact,
                _evaluated == 0 ? 0 : Math.Round((double)_exact / _evaluated, 4),
                macroCount == 0 ? 0 : Math.Round(macroSum / macroCount, 4),
                Math.Round(microPrecision, 4),
                Math.Round(microRecall, 4),
                Math.Round(microF1, 4),
                _primaryEvaluated,
                _primaryEvaluated == 0 ? 0 : Math.Round((double)_primaryCorrect / _primaryEvaluated, 4),
                _unclassified,
                _evaluated == 0 ? 0 : Math.Round((double)_unclassified / _evaluated, 4),
                _expectedEmpty,
                _falsePositiveCategories,
                perCategory);
        }
    }
}
