using System.Text.RegularExpressions;

namespace Convenciones.Worker.Extraction;

public sealed record MergedClause(
    int Ordinal,
    string? Number,
    string? Heading,
    string Text,
    int PageFrom,
    int PageTo,
    double Confidence,
    bool NeedsReview);

/// <summary>
/// Merges chunk results in document order, dropping overlap duplicates and flagging conflicting
/// duplicates for review. Ordinals are assigned only after the merge, so they are gap-free.
/// </summary>
public static partial class ClauseMerger
{
    public static IReadOnlyList<MergedClause> Merge(IEnumerable<ParsedClause> clauses)
    {
        var merged = new List<MergedClause>();
        var seenText = new HashSet<string>(StringComparer.Ordinal);
        var numberToText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var clause in clauses)
        {
            var normalized = Normalize(clause.Text);
            if (normalized.Length == 0 || !seenText.Add(normalized))
            {
                continue;
            }

            var needsReview = false;
            if (!string.IsNullOrWhiteSpace(clause.Number))
            {
                if (numberToText.TryGetValue(clause.Number.Trim(), out var previous) && previous != normalized)
                {
                    needsReview = true;
                }
                else
                {
                    numberToText[clause.Number.Trim()] = normalized;
                }
            }

            merged.Add(new MergedClause(
                merged.Count + 1,
                clause.Number,
                clause.Heading,
                clause.Text,
                clause.PageFrom,
                clause.PageTo,
                clause.Confidence,
                needsReview));
        }

        return merged;
    }

    private static string Normalize(string text) =>
        WhitespaceRegex().Replace(text.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
