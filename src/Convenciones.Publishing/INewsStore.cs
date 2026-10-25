namespace Convenciones.Publishing;

/// <summary>
/// What the publishing module needs from persistence.
/// </summary>
/// <remarks>
/// News is not company-scoped: the schema has no <c>company_id</c> on either table, because published
/// news is public and the administrator's company is not part of the content. There is therefore no
/// tenant parameter here — unlike agreements and negotiation cases.
/// </remarks>
public interface INewsStore
{
    Task<NewsArticle?> FindAsync(Guid articleId, CancellationToken cancellationToken);

    Task<bool> CategoryExistsAsync(Guid categoryId, CancellationToken cancellationToken);

    Task<string?> CategoryNameAsync(Guid categoryId, CancellationToken cancellationToken);

    void Add(NewsArticle article);

    void Add(NewsCategory category);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
