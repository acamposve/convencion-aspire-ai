using Microsoft.EntityFrameworkCore;

namespace Convenciones.Legal;

/// <summary>
/// The read side of the legal library: one method per use case, each projecting straight into a read
/// model.
/// </summary>
/// <remarks>
/// <para>
/// The methods take <see cref="IQueryable{T}"/> instead of a DbContext, exactly as
/// <c>ComparisonQueries</c> does: the module then has no way to write, and the caller is forced to
/// pick <c>AsNoTracking</c>. Nothing here loads the aggregate through the change tracker, which is
/// what separates these queries from the commands in <see cref="StatuteCommands"/>.
/// </para>
/// <para>
/// Ordering is stable and documented: statutes by kind (the labour statute first, because the
/// database orders the enum by declaration) then by name; articles by integer number. The legacy
/// ordered articles by a varchar, producing 1, 10, 100, 11, 2.
/// </para>
/// </remarks>
public static class StatuteQueries
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>
    /// The comparison workspace reads at most this many statute articles per heading. A bound exists
    /// because one request must not be able to return the whole corpus; the honest total is returned
    /// alongside the page so a truncated answer is visible rather than silent.
    /// </summary>
    public const int MaxComparisonArticles = 100;

    public static async Task<StatuteListResponse> ListAsync(
        IQueryable<Statute> statutes,
        StatuteKind? kind,
        string? term,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statutes);

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        var query = statutes.AsNoTracking();

        if (kind is { } wanted)
        {
            query = query.Where(statute => statute.Kind == wanted);
        }

        if (!string.IsNullOrWhiteSpace(term))
        {
            var pattern = $"%{term.Trim()}%";
            query = query.Where(statute => EF.Functions.ILike(statute.Name, pattern));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await query
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

    public static async Task<StatuteDetail?> DetailAsync(
        IQueryable<Statute> statutes,
        Guid statuteId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statutes);

        var statute = await statutes
            .AsNoTracking()
            .Include(entity => entity.Articles)
            .SingleOrDefaultAsync(entity => entity.Id == statuteId, cancellationToken)
            .ConfigureAwait(false);

        return statute is null ? null : StatuteResponses.Detail(statute);
    }

    /// <summary>
    /// The articles of one statute, ordered numerically.
    /// </summary>
    /// <returns><see langword="null"/> when the statute does not exist — an empty article list and a
    /// missing statute must not be confused, so existence is asked separately.</returns>
    public static async Task<StatuteArticleListResponse?> ArticlesAsync(
        IQueryable<Statute> statutes,
        Guid statuteId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statutes);

        var articles = await statutes
            .AsNoTracking()
            .Where(statute => statute.Id == statuteId)
            .SelectMany(statute => statute.Articles)
            .OrderBy(article => article.Number)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (articles.Count == 0
            && !await statutes.AsNoTracking()
                .AnyAsync(statute => statute.Id == statuteId, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        return new StatuteArticleListResponse(
            articles.Select(StatuteResponses.Article).ToArray(),
            articles.Count);
    }

    public static async Task<StatuteArticleResponse?> ArticleAsync(
        IQueryable<Statute> statutes,
        Guid statuteId,
        Guid articleId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statutes);

        var article = await statutes
            .AsNoTracking()
            .Where(statute => statute.Id == statuteId)
            .SelectMany(statute => statute.Articles)
            .SingleOrDefaultAsync(candidate => candidate.Id == articleId, cancellationToken)
            .ConfigureAwait(false);

        return article is null ? null : StatuteResponses.Article(article);
    }

    /// <summary>
    /// The statute articles classified under one comparison heading — what the comparison workspace
    /// shows beside the agreements.
    /// </summary>
    /// <remarks>
    /// Statutes are company-independent, so the query carries no company predicate. That is the whole
    /// point: a caller of any company reads the same legal corpus, and an excluded heading yields no
    /// articles because the caller never reaches this method for one.
    /// </remarks>
    public static async Task<StatuteArticlePage> ForComparisonAsync(
        IQueryable<StatuteArticle> articles,
        IQueryable<Statute> statutes,
        Guid headingId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(statutes);

        var query =
            from article in articles.AsNoTracking()
            join statute in statutes.AsNoTracking() on article.StatuteId equals statute.Id
            where article.HeadingId == headingId
            select new { Article = article, Statute = statute };

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await query
            .OrderBy(row => row.Statute.Name)
            .ThenBy(row => row.Article.Number)
            .Take(MaxComparisonArticles)
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

/// <summary>The bounded page of statute articles for one comparison heading, with the real total.</summary>
public sealed record StatuteArticlePage(
    IReadOnlyList<StatuteComparisonArticle> Items,
    int Total);
