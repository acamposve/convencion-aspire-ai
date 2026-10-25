using Convenciones.Publishing;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Infrastructure;

/// <summary>The API's implementation of the publishing module's persistence port.</summary>
/// <remarks>
/// News is not company-scoped: the schema has no <c>company_id</c>, because published news is public.
/// The public reads are protected by role and RLS, not by a tenant predicate here.
/// </remarks>
public sealed class NewsStore(ConvencionesDbContext db) : INewsStore
{
    public Task<NewsArticle?> FindAsync(Guid articleId, CancellationToken cancellationToken) =>
        db.NewsArticles.SingleOrDefaultAsync(article => article.Id == articleId, cancellationToken);

    public Task<bool> CategoryExistsAsync(Guid categoryId, CancellationToken cancellationToken) =>
        db.NewsCategories
            .AsNoTracking()
            .AnyAsync(category => category.Id == categoryId, cancellationToken);

    public Task<string?> CategoryNameAsync(Guid categoryId, CancellationToken cancellationToken) =>
        db.NewsCategories
            .AsNoTracking()
            .Where(category => category.Id == categoryId)
            .Select(category => category.Name)
            .SingleOrDefaultAsync(cancellationToken);

    public void Add(NewsArticle article) => db.NewsArticles.Add(article);

    public void Add(NewsCategory category) => db.NewsCategories.Add(category);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
