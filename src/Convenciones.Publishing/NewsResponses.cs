using Convenciones.Agreements;
using Convenciones.Catalog;

namespace Convenciones.Publishing;

/// <summary>
/// Maps the publishing entities to the response records.
/// </summary>
/// <remarks>
/// The public records are separate types from the admin ones on purpose: the allow-list is the type
/// definition, so a field added to <see cref="NewsDetail"/> cannot leak into the portal by accident. The
/// public agreement projection composes the title with the same helper the internal screens use (Q-1).
/// </remarks>
public static class NewsResponses
{
    public static NewsDetail Detail(NewsArticle article, string categoryName) =>
        new(
            article.Id,
            article.CategoryId,
            categoryName,
            article.Title,
            article.Summary,
            article.Body,
            article.State.ToString(),
            article.PublishedAt,
            article.CreatedAt,
            article.UpdatedAt);

    public static NewsSummary Summary(NewsArticle article, string categoryName) =>
        new(
            article.Id,
            article.Title,
            article.Summary,
            article.CategoryId,
            categoryName,
            article.State.ToString(),
            article.PublishedAt,
            article.UpdatedAt);

    public static NewsCategoryResponse Category(NewsCategory category) =>
        new(category.Id, category.Name, category.Description, category.SortOrder, category.IsActive);

    public static PublicNewsSummary PublicSummary(NewsArticle article, string categoryName) =>
        new(article.Id, article.Title, article.Summary, categoryName, Published(article));

    public static PublicNewsDetail PublicDetail(NewsArticle article, string categoryName) =>
        new(article.Id, article.Title, article.Summary, RichText.Sanitize(article.Body), categoryName, Published(article));

    /// <summary>
    /// Rich text is sanitised again at the public boundary. The write path already sanitises (A-33), but the
/// public surface may serve rows written before that rule existed, and it is the one place where a miss
/// becomes a stored-XSS on an unauthenticated page. Re-sanitising is cheap and keeps AC-S10 true whatever
/// the provenance of the row.
///
/// The title is composed, never stored, so a company rename changes it on the public page too.
    /// </summary>
    public static PublicAgreementSummary PublicSummary(Agreement agreement, string companyName) =>
        new(
            agreement.Id,
            agreement.ComposeTitle(companyName),
            companyName,
            agreement.ValidFrom,
            agreement.ValidTo,
            agreement.Scope,
            agreement.PublishedAt ?? DateTimeOffset.MinValue);

    public static PublicAgreementDetail PublicDetail(Agreement agreement, string companyName) =>
        new(
            agreement.Id,
            agreement.ComposeTitle(companyName),
            companyName,
            agreement.ValidFrom,
            agreement.ValidTo,
            agreement.Scope,
            agreement.PublishedAt ?? DateTimeOffset.MinValue,
            agreement.Clauses
                .OrderBy(clause => clause.Number)
                .Select(clause => new PublicClause(clause.Number, clause.Title, clause.Summary, RichText.Sanitize(clause.Body)))
                .ToArray(),
            agreement.Annexes
                .OrderBy(annex => annex.SortOrder)
                .ThenBy(annex => annex.Name, StringComparer.OrdinalIgnoreCase)
                .Select(annex => new PublicAnnex(annex.Name, annex.Description, annex.Body is null ? null : RichText.Sanitize(annex.Body)))
                .ToArray());

    /// <summary>
    /// RLS guarantees a published article, so the timestamp exists. A null here would mean the anonymous
    /// policy is broken, and failing loudly is better than inventing a date.
    /// </summary>
    private static DateTimeOffset Published(NewsArticle article) =>
        article.PublishedAt
        ?? throw new InvalidOperationException(
            $"News article '{article.Id}' was exposed without a publication timestamp.");
}
