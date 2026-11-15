namespace Convenciones.Evaluation;

public sealed record AnnotatedClause(string Text, int PageFrom, int PageTo);

public sealed record PredictedClause(string Text, int PageFrom, int PageTo);

public sealed record BoundaryResult(
    int Annotated,
    int Predicted,
    int Matched,
    double Precision,
    double Recall,
    double F1);

/// <summary>A matched annotation/prediction pair, by index into the two input lists.</summary>
public sealed record ClauseMatch(int AnnotatedIndex, int PredictedIndex);

/// <summary>
/// Clause-boundary comparison: greedy matching of each annotation to the unused prediction with the
/// highest longest-common-substring ratio. A ratio of 0.8 means the predicted text covers at least
/// 80% of the annotated text in order.
/// </summary>
public static class BoundaryMetrics
{
    public static IReadOnlyList<ClauseMatch> Match(
        IReadOnlyList<AnnotatedClause> annotated,
        IReadOnlyList<PredictedClause> predicted,
        double threshold = 0.8)
    {
        ArgumentNullException.ThrowIfNull(annotated);
        ArgumentNullException.ThrowIfNull(predicted);

        var used = new bool[predicted.Count];
        var matches = new List<ClauseMatch>();

        for (var annotationIndex = 0; annotationIndex < annotated.Count; annotationIndex++)
        {
            var bestIndex = -1;
            var bestRatio = 0.0;

            for (var index = 0; index < predicted.Count; index++)
            {
                if (used[index])
                {
                    continue;
                }

                var ratio = OverlapRatio(annotated[annotationIndex].Text, predicted[index].Text);
                if (ratio > bestRatio)
                {
                    bestRatio = ratio;
                    bestIndex = index;
                }
            }

            if (bestIndex >= 0 && bestRatio >= threshold)
            {
                used[bestIndex] = true;
                matches.Add(new ClauseMatch(annotationIndex, bestIndex));
            }
        }

        return matches;
    }

    public static BoundaryResult Compute(
        IReadOnlyList<AnnotatedClause> annotated,
        IReadOnlyList<PredictedClause> predicted,
        double threshold = 0.8)
    {
        ArgumentNullException.ThrowIfNull(annotated);
        ArgumentNullException.ThrowIfNull(predicted);

        var matched = Match(annotated, predicted, threshold).Count;

        var precision = predicted.Count == 0 ? 0 : (double)matched / predicted.Count;
        var recall = annotated.Count == 0 ? 0 : (double)matched / annotated.Count;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

        return new BoundaryResult(
            annotated.Count,
            predicted.Count,
            matched,
            Math.Round(precision, 4),
            Math.Round(recall, 4),
            Math.Round(f1, 4));
    }

    /// <summary>Longest common substring length divided by the annotation length.</summary>
    internal static double OverlapRatio(string annotated, string predicted)
    {
        if (annotated.Length == 0 || predicted.Length == 0)
        {
            return 0;
        }

        var previous = new int[predicted.Length + 1];
        var current = new int[predicted.Length + 1];
        var longest = 0;

        foreach (var left in annotated)
        {
            for (var rightIndex = 0; rightIndex < predicted.Length; rightIndex++)
            {
                current[rightIndex + 1] = left == predicted[rightIndex]
                    ? previous[rightIndex] + 1
                    : 0;
                longest = Math.Max(longest, current[rightIndex + 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return (double)longest / annotated.Length;
    }
}

public sealed record ClassificationAnnotation(int ClauseIndex, IReadOnlySet<string> Categories);

public sealed record ClassificationResult(
    double MacroF1,
    double MicroF1,
    IReadOnlyDictionary<string, double> PerCategory);

/// <summary>Per-category precision/recall/F1, macro-averaged over categories with support.</summary>
public static class ClassificationMetrics
{
    public static ClassificationResult Compute(
        IReadOnlyList<ClassificationAnnotation> annotated,
        IReadOnlyList<IReadOnlySet<string>> predicted)
    {
        ArgumentNullException.ThrowIfNull(annotated);
        ArgumentNullException.ThrowIfNull(predicted);

        var categories = annotated
            .SelectMany(item => item.Categories)
            .Concat(predicted.SelectMany(item => item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var perCategory = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var macro = 0.0;
        var counted = 0;
        var truePositives = 0;
        var falsePositives = 0;
        var falseNegatives = 0;

        foreach (var category in categories)
        {
            var tp = 0;
            var fp = 0;
            var fn = 0;

            for (var index = 0; index < annotated.Count; index++)
            {
                var expected = annotated[index].Categories.Contains(category);
                var actual = index < predicted.Count && predicted[index].Contains(category);

                if (expected && actual) tp++;
                else if (actual) fp++;
                else if (expected) fn++;
            }

            truePositives += tp;
            falsePositives += fp;
            falseNegatives += fn;

            var precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
            var recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

            if (tp + fn > 0)
            {
                perCategory[category] = Math.Round(f1, 4);
                macro += f1;
                counted++;
            }
        }

        var microPrecision = truePositives + falsePositives == 0
            ? 0
            : (double)truePositives / (truePositives + falsePositives);
        var microRecall = truePositives + falseNegatives == 0
            ? 0
            : (double)truePositives / (truePositives + falseNegatives);
        var micro = microPrecision + microRecall == 0
            ? 0
            : 2 * microPrecision * microRecall / (microPrecision + microRecall);

        return new ClassificationResult(
            counted == 0 ? 0 : Math.Round(macro / counted, 4),
            Math.Round(micro, 4),
            perCategory);
    }
}
