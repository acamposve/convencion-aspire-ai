using System.Globalization;
using System.Text;
using UglyToad.PdfPig;

namespace Convenciones.Evaluation;

/// <summary>Where a clause actually sits in the rendered PDF, next to what the draft annotation claimed.</summary>
public sealed record ClausePageSpan(
    string DocumentId,
    string ClauseId,
    int DraftFrom,
    int DraftTo,
    int? PdfFrom,
    int? PdfTo)
{
    public bool Found => PdfFrom is not null && PdfTo is not null;

    public bool MatchesDraft => Found && PdfFrom == DraftFrom && PdfTo == DraftTo;
}

/// <summary>
/// Locates each clause in the rendered PDFs by text, so page ranges come from the document rather than
/// from a draft annotation written before the PDFs existed.
/// </summary>
public static class PdfPageLocator
{
    public static IReadOnlyList<ClausePageSpan> Locate(EvaluationDataset dataset, string pdfDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfDirectory);

        var spans = new List<ClausePageSpan>();

        foreach (var document in dataset.Documents)
        {
            var path = Path.Combine(pdfDirectory, document.DocumentId + ".pdf");
            if (!File.Exists(path))
            {
                throw new EvaluationInputException($"Missing PDF for document '{document.DocumentId}': {path}");
            }

            var pages = RenderPages(path);
            foreach (var clause in document.Clauses)
            {
                var located = Find(clause.Text, pages);
                spans.Add(new ClausePageSpan(
                    document.DocumentId,
                    clause.ClauseId,
                    clause.PageFrom,
                    clause.PageTo,
                    located?.From,
                    located?.To));
            }
        }

        return spans;
    }

    internal static IReadOnlyList<string> RenderPages(string path)
    {
        using var document = PdfDocument.Open(path);

        // page.Text joins glyph runs without separating words that end a line ("alliindicados"); words
        // carry positions, so joining them with a space rebuilds the token boundaries.
        return document.GetPages()
            .Select(page => string.Join(' ', page.GetWords().Select(word => word.Text)))
            .ToList();
    }

    /// <summary>Returns the 1-based page span that contains the clause text, or null when not found.</summary>
    internal static (int From, int To)? Find(string clauseText, IReadOnlyList<string> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0 || string.IsNullOrWhiteSpace(clauseText))
        {
            return null;
        }

        var builder = new StringBuilder();
        var ranges = new List<(int Start, int End, int Page)>();

        for (var index = 0; index < pages.Count; index++)
        {
            var normalized = Normalize(pages[index]);
            ranges.Add((builder.Length, builder.Length + normalized.Length, index + 1));
            builder.Append(normalized).Append(' ');
        }

        var combined = builder.ToString();
        var needle = Normalize(clauseText);
        if (needle.Length == 0)
        {
            return null;
        }

        var start = combined.IndexOf(needle, StringComparison.Ordinal);
        if (start >= 0)
        {
            return Resolve(ranges, start, start + needle.Length - 1);
        }

        // Fall back to the clause edges: a clause may be broken by a page boundary or a ligature.
        var head = needle[..Math.Min(60, needle.Length)];
        var tail = needle[^Math.Min(60, needle.Length)..];
        var headIndex = combined.IndexOf(head, StringComparison.Ordinal);
        var tailIndex = combined.LastIndexOf(tail, StringComparison.Ordinal);

        if (headIndex < 0 || tailIndex < 0 || tailIndex < headIndex)
        {
            return null;
        }

        return Resolve(ranges, headIndex, tailIndex + tail.Length - 1);
    }

    private static (int From, int To)? Resolve(
        List<(int Start, int End, int Page)> ranges,
        int start,
        int end)
    {
        int? fromPage = null;
        int? toPage = null;

        foreach (var range in ranges)
        {
            if (fromPage is null && start >= range.Start && start < range.End)
            {
                fromPage = range.Page;
            }

            if (end >= range.Start && end < range.End)
            {
                toPage = range.Page;
            }
        }

        return fromPage is null || toPage is null ? null : (fromPage.Value, toPage.Value);
    }

    /// <summary>Case, accents and whitespace must not decide whether a clause is found.</summary>
    /// <remarks>
    /// Uses an explicit mapping rather than <c>string.Normalize(FormD)</c>: this process runs with
    /// invariant globalization, where Unicode normalization is a no-op and accented characters are
    /// returned unchanged.
    /// </remarks>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(StripAccent(char.ToLowerInvariant(character)));
        }

        return builder.ToString().Trim();
    }

    private static char StripAccent(char character) => character switch
    {
        'á' or 'à' or 'ä' or 'â' or 'ã' => 'a',
        'é' or 'è' or 'ë' or 'ê' => 'e',
        'í' or 'ì' or 'ï' or 'î' => 'i',
        'ó' or 'ò' or 'ö' or 'ô' or 'õ' => 'o',
        'ú' or 'ù' or 'ü' or 'û' => 'u',
        'ñ' => 'n',
        'ç' => 'c',
        _ => character,
    };
}
