using Microsoft.EntityFrameworkCore;

namespace Convenciones.Legal;

/// <summary>Paging bounds for the legal library, in one place so the endpoint and the handlers agree.</summary>
public static class StatutePaging
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    /// <summary>
    /// The comparison workspace reads at most this many statute articles per heading. A bound exists
    /// because one request must not be able to return the whole corpus; the number of articles in
    /// scope is returned alongside, so a truncated answer is visible rather than silent.
    /// </summary>
    public const int MaxComparisonArticles = 100;
}

// ---- Queries ------------------------------------------------------------------------------------

public sealed record ListStatutesQuery(StatuteKind? Kind, string? Term, int Page, int PageSize);

public sealed record GetStatuteQuery(Guid StatuteId);

public sealed record ListStatuteArticlesQuery(Guid StatuteId);

public sealed record GetStatuteArticleQuery(Guid StatuteId, Guid ArticleId);

public sealed record GetStatuteArticlesForHeadingQuery(Guid HeadingId);

/// <summary>
/// The read side of the legal library: one handler type per use case, each owning its query and its
/// response, and each projecting straight into a read model.
/// </summary>
/// <remarks>
/// <para>
/// Constitution Art. IV.4 requires queries as distinct handler types that never share a model with a
/// command, and the handlers take <see cref="IQueryable{T}"/> instead of a DbContext: the module then
/// has no way to write and the caller is forced to pick <c>AsNoTracking</c>. Nothing here loads the
/// aggregate through the change tracker, which is what separates these queries from the commands in
/// <see cref="StatuteCommands"/>.
/// </para>
/// <para>
/// Ordering is stable and documented: statutes by kind (the labour statute first, because the
/// database orders the enum by declaration) then by name; articles by integer number. The legacy
/// ordered articles by a varchar, producing 1, 10, 100, 11, 2.
/// </para>
/// </remarks>
public sealed class ListStatutesHandler
{
    public async Task<StatuteListResponse> HandleAsync(
        IQueryable<Statute> statutes,
        ListStatutesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statutes);
        ArgumentNullException.ThrowIfNull(query);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > StatutePaging.MaxPageSize
            ? StatutePaging.DefaultPageSize
            : query.PageSize;

        var filtered = statutes.AsNoTracking();

        if (query.Kind is { } wanted)
        {
            filtered = filtered.Where(statute => statute.Kind == wanted);
        }

        if (!string.IsNullOrWhiteSpace(query.Term))
        {
            var pattern = $"%{query.Term.Trim()}%";
            filtered = filtered.Where(statute => EF.Functions.ILike(statute.Name, pattern));
        }

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await filtered
            .OrderBy(statute => statute.Kind)
            .ThenBy(statute => statute.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(statute => new StatuteSummaryRow(
                statute.Id,
                statute.Kind,
                statute.Name,
                statute.PublishedOn,
                statute.Source,
                statute.DocumentId,
                statute.Articles.Count,
                statute.RowVersion))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows.Select(StatuteResponses.Summary).ToArray();
        return new StatuteListResponse(items, page, pageSize, total);
    }
}

public sealed class GetStatuteHandler
{
    /// <returns><see langword="null"/> when no statute has that id.</returns>
    public async Task<StatuteDetail?> HandleAsync(
        IQueryable<Statute> statutes,
        GetStatuteQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statutes);
        ArgumentNullException.ThrowIfNull(query);

        var statute = await statutes
            .AsNoTracking()
            .Include(entity => entity.Articles)
            .SingleOrDefaultAsync(entity => entity.Id == query.StatuteId, cancellationToken)
            .ConfigureAwait(false);

        return statute is null ? null : StatuteResponses.Detail(statute);
    }
}

public sealed class ListStatuteArticlesHandler
{
    /// <returns><see langword="null"/> when the statute does not exist — an empty article list and a
    /// missing statute must not be confused, so existence is asked separately.</returns>
    public async Task<StatuteArticleListResponse?> HandleAsync(
        IQueryable<Statute> statutes,
        ListStatuteArticlesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statutes);
        ArgumentNullException.ThrowIfNull(query);

        var articles = await statutes
            .AsNoTracking()
            .Where(statute => statute.Id == query.StatuteId)
            .SelectMany(statute => statute.Articles)
            .OrderBy(article => article.Number)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (articles.Count == 0
            && !await statutes.AsNoTracking()
                .AnyAsync(statute => statute.Id == query.StatuteId, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        return new StatuteArticleListResponse(
            articles.Select(StatuteResponses.Article).ToArray(),
            articles.Count);
    }
}

public sealed class GetStatuteArticleHandler
{
    /// <returns><see langword="null"/> when that article is not part of that statute.</returns>
    public async Task<StatuteArticleResponse?> HandleAsync(
        IQueryable<Statute> statutes,
        GetStatuteArticleQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statutes);
        ArgumentNullException.ThrowIfNull(query);

        var article = await statutes
            .AsNoTracking()
            .Where(statute => statute.Id == query.StatuteId)
            .SelectMany(statute => statute.Articles)
            .SingleOrDefaultAsync(candidate => candidate.Id == query.ArticleId, cancellationToken)
            .ConfigureAwait(false);

        return article is null ? null : StatuteResponses.Article(article);
    }
}

/// <summary>
/// The statute articles classified under one comparison heading — what the comparison workspace shows
/// beside the agreements.
/// </summary>
/// <remarks>
/// Statutes are company-independent, so the query carries no company predicate: a caller of any
/// company reads the same legal corpus. The array is bounded by
/// <see cref="StatutePaging.MaxComparisonArticles"/> and <see cref="StatuteArticlePage.Total"/> is the
/// number of articles in scope for the projection (0 when the caller never asks — an excluded heading
/// is served without reaching this handler), so truncation is visible.
/// </remarks>
public sealed class GetStatuteArticlesForHeadingHandler
{
    public async Task<StatuteArticlePage> HandleAsync(
        IQueryable<StatuteArticle> articles,
        IQueryable<Statute> statutes,
        GetStatuteArticlesForHeadingQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(statutes);
        ArgumentNullException.ThrowIfNull(query);

        var joined =
            from article in articles.AsNoTracking()
            join statute in statutes.AsNoTracking() on article.StatuteId equals statute.Id
            where article.HeadingId == query.HeadingId
            select new { Article = article, Statute = statute };

        var total = await joined.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await joined
            .OrderBy(row => row.Statute.Name)
            .ThenBy(row => row.Article.Number)
            .Take(StatutePaging.MaxComparisonArticles)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(row => new StatuteComparisonArticle(
                row.Statute.Id,
                row.Statute.Name,
                row.Statute.Kind.ToString(),
                row.Article.Id,
                row.Article.Number,
                row.Article.Title,
                row.Article.Body,
                row.Article.EconomicComparisonNote))
            .ToArray();

        return new StatuteArticlePage(items, total);
    }
}

/// <summary>
/// The bounded page of statute articles for one comparison heading.
/// </summary>
/// <param name="Items">At most <see cref="StatutePaging.MaxComparisonArticles"/> articles, in stable
/// order.</param>
/// <param name="Total">How many articles the heading selects. When it exceeds
/// <paramref name="Items"/>' count, the response was truncated by the bound.</param>
public sealed record StatuteArticlePage(
    IReadOnlyList<StatuteComparisonArticle> Items,
    int Total);
