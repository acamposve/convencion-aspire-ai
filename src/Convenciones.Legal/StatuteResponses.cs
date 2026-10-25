namespace Convenciones.Legal;

/// <summary>
/// Maps entities to the response records, in one place so every route answers with the same shape.
/// </summary>
/// <remarks>
/// Enums are rendered by name — the API contract forbids numeric enum values in JSON — and articles
/// are ordered numerically, never lexically: the legacy ordering produced 1, 10, 100, 11, 2.
/// </remarks>
public static class StatuteResponses
{
    public static StatuteDetail Detail(Statute statute)
    {
        ArgumentNullException.ThrowIfNull(statute);

        return new StatuteDetail(
            statute.Id,
            statute.Kind.ToString(),
            statute.Name,
            statute.PublishedOn,
            statute.Source,
            statute.DocumentId,
            statute.CreatedAt,
            statute.UpdatedAt,
            statute.RowVersion,
            statute.Articles
                .OrderBy(article => article.Number)
                .Select(Article)
                .ToArray());
    }

    public static StatuteSummary Summary(StatuteSummaryRow row) =>
        new(
            row.Id,
            row.Kind.ToString(),
            row.Name,
            row.PublishedOn,
            row.Source,
            row.DocumentId,
            row.ArticleCount,
            row.RowVersion);

    public static StatuteArticleResponse Article(StatuteArticle article)
    {
        ArgumentNullException.ThrowIfNull(article);

        return new StatuteArticleResponse(
            article.Id,
            article.Number,
            article.Title,
            article.Summary,
            article.Body,
            article.HeadingId,
            article.EconomicComparisonNote,
            article.RowVersion);
    }
}

/// <summary>
/// The list projection before the kind is rendered: <see cref="StatuteKind"/> cannot be interpolated
/// into a SQL projection as a name, so the row carries the enum and the mapping happens in memory.
/// </summary>
public sealed record StatuteSummaryRow(
    Guid Id,
    StatuteKind Kind,
    string Name,
    DateOnly? PublishedOn,
    string? Source,
    Guid? DocumentId,
    int ArticleCount,
    uint RowVersion);
