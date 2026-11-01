namespace Convenciones.Ai.DocumentIntelligence;

/// <summary>One span of text in the document's global content.</summary>
public sealed record LayoutSpan(int Offset, int Length);

/// <summary>Bounding box in the page's coordinate system.</summary>
public sealed record LayoutBoundingBox(double X, double Y, double Width, double Height);

/// <summary>A paragraph with its role (title, section heading, …) and source location.</summary>
public sealed record LayoutParagraph(
    string? Role,
    string Content,
    int PageNumber,
    LayoutBoundingBox? BoundingBox,
    IReadOnlyList<LayoutSpan> Spans);

/// <summary>One page: text, paragraphs and the map from normalised to original offsets.</summary>
public sealed record LayoutPage(
    int PageNumber,
    double Width,
    double Height,
    string Text,
    string NormalizedText,
    IReadOnlyList<int> NormalizedToOriginalOffsets,
    IReadOnlyList<LayoutParagraph> Paragraphs,
    IReadOnlyList<LayoutSpan> Spans);

/// <summary>
/// The structure Document Intelligence returns, plus the signal the caller needs about scanned PDFs.
/// </summary>
/// <remarks>
/// <see cref="RequiresOcr"/> is true when the average non-whitespace characters per page fall below the
/// configured threshold. A page with no text is never reported as a successful extraction.
/// </remarks>
public sealed record DocumentLayout(
    int PageCount,
    IReadOnlyList<LayoutPage> Pages,
    bool RequiresOcr,
    int CharactersPerPage,
    TimeSpan Latency);
