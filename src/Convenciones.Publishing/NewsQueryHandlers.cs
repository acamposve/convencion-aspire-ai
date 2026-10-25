using Convenciones.Agreements;
using Convenciones.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Publishing;

public sealed record ListNewsQuery(PublicationState? State, Guid? CategoryId, int Page, int PageSize);

/// <summary>
/// The read side of the publishing module: admin lists and the public projections.
/// </summary>
/// <remarks>
/// The public handlers filter by **id or paging only** — never by publication state. That is deliberate:
/// the anonymous database role and its RLS policies are what hide drafts, so the integration test that
/// requests a draft by id proves the database is the guard rather than the handler
/// (<c>security-model.md</c> AC-S7, T12 AC 1 and AC 5).
/// </remarks>
public sealed class ListNewsHandler
{
    public async Task<NewsListResponse> HandleAsync(
        IQueryable<NewsArticle> articles,
        IQueryable<NewsCategory> categories,
        ListNewsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = Paging(query.Page, query.PageSize);
        var filtered = articles.AsNoTracking();

        if (query.State is { } state)
        {
            filtered = filtered.Where(article => article.State == state);
        }

        if (query.CategoryId is { } categoryId)
        {
            filtered = filtered.Where(article => article.CategoryId == categoryId);
        }

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await (from article in filtered
                          join category in categories.AsNoTracking() on article.CategoryId equals category.Id
                          orderby article.UpdatedAt descending, article.Id
                          select new { Article = article, CategoryName = category.Name })
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new NewsListResponse(
            rows.Select(row => NewsResponses.Summary(row.Article, row.CategoryName)).ToArray(),
            page,
            pageSize,
            total);
    }

    internal static (int Page, int PageSize) Paging(int page, int pageSize) =>
        (page < 1 ? 1 : page,
         pageSize is < 1 or > PublishingPaging.MaxPageSize ? PublishingPaging.DefaultPageSize : pageSize);
}

public sealed class GetNewsHandler
{
    public async Task<NewsDetail?> HandleAsync(
        IQueryable<NewsArticle> articles,
        IQueryable<NewsCategory> categories,
        Guid articleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(categories);

        var row = await (from article in articles.AsNoTracking()
                         join category in categories.AsNoTracking() on article.CategoryId equals category.Id
                         where article.Id == articleId
                         select new { Article = article, CategoryName = category.Name })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : NewsResponses.Detail(row.Article, row.CategoryName);
    }
}

public sealed class ListNewsCategoriesHandler
{
    public async Task<IReadOnlyList<NewsCategoryResponse>> HandleAsync(
        IQueryable<NewsCategory> categories,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(categories);

        var query = categories.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(category => category.IsActive);
        }

        var rows = await query
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(NewsResponses.Category).ToArray();
    }
}

// ---- Public reads --------------------------------------------------------------------------------

public sealed class ListPublicNewsHandler
{
    public async Task<PublicNewsListResponse> HandleAsync(
        IQueryable<NewsArticle> articles,
        IQueryable<NewsCategory> categories,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(categories);

        var (safePage, safePageSize) = ListNewsHandler.Paging(page, pageSize);

        // No state predicate: RLS already returned only published rows.
        var total = await articles.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await (from article in articles.AsNoTracking()
                          join category in categories.AsNoTracking() on article.CategoryId equals category.Id
                          orderby article.PublishedAt descending, article.Id
                          select new { Article = article, CategoryName = category.Name })
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PublicNewsListResponse(
            rows.Select(row => NewsResponses.PublicSummary(row.Article, row.CategoryName)).ToArray(),
            safePage,
            safePageSize,
            total);
    }
}

public sealed class GetPublicNewsHandler
{
    public async Task<PublicNewsDetail?> HandleAsync(
        IQueryable<NewsArticle> articles,
        IQueryable<NewsCategory> categories,
        Guid articleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(categories);

        var row = await (from article in articles.AsNoTracking()
                         join category in categories.AsNoTracking() on article.CategoryId equals category.Id
                         where article.Id == articleId
                         select new { Article = article, CategoryName = category.Name })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : NewsResponses.PublicDetail(row.Article, row.CategoryName);
    }
}

public sealed class ListPublicAgreementsHandler
{
    public async Task<PublicAgreementListResponse> HandleAsync(
        IQueryable<Agreement> agreements,
        IQueryable<Company> companies,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agreements);
        ArgumentNullException.ThrowIfNull(companies);

        var (safePage, safePageSize) = ListNewsHandler.Paging(page, pageSize);

        // No state predicate: RLS already returned only published agreements.
        var total = await agreements.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await (from agreement in agreements.AsNoTracking()
                          join company in companies.AsNoTracking() on agreement.CompanyId equals company.Id
                          orderby agreement.PublishedAt descending, agreement.Id
                          select new { Agreement = agreement, CompanyName = company.Name })
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PublicAgreementListResponse(
            rows.Select(row => NewsResponses.PublicSummary(row.Agreement, row.CompanyName)).ToArray(),
            safePage,
            safePageSize,
            total);
    }
}

public sealed class GetPublicAgreementHandler
{
    public async Task<PublicAgreementDetail?> HandleAsync(
        IQueryable<Agreement> agreements,
        IQueryable<Company> companies,
        Guid agreementId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agreements);
        ArgumentNullException.ThrowIfNull(companies);

        // No state predicate: RLS hides a draft agreement even when its id is known (AC-S7).
        var agreement = await agreements
            .AsNoTracking()
            .Include(entity => entity.Clauses)
            .Include(entity => entity.Annexes)
            .Where(entity => entity.Id == agreementId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null)
        {
            return null;
        }

        var companyName = await companies
            .AsNoTracking()
            .Where(company => company.Id == agreement.CompanyId)
            .Select(company => company.Name)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return NewsResponses.PublicDetail(agreement, companyName ?? "Empresa");
    }
}
