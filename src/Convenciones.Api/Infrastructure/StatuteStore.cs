using Convenciones.Legal;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// The API's implementation of the legal library's persistence port.
/// </summary>
/// <remarks>
/// The module never sees <see cref="ConvencionesDbContext"/>: it declares what a command needs from
/// persistence and this class supplies it. That is what keeps the legal library a module rather than
/// a folder of code that reaches into the host's context (constitution Art. IV.6).
/// </remarks>
public sealed class StatuteStore(ConvencionesDbContext db) : IStatuteStore
{
    /// <summary>
    /// Tracked, with its articles: a command loads the aggregate before changing it. Reads never go
    /// through here — they project directly in the query handlers.
    /// </summary>
    public Task<Statute?> FindAsync(Guid statuteId, CancellationToken cancellationToken) =>
        db.Statutes
            .Include(statute => statute.Articles)
            .SingleOrDefaultAsync(statute => statute.Id == statuteId, cancellationToken);

    public Task<bool> HeadingIsActiveAsync(Guid headingId, CancellationToken cancellationToken) =>
        db.ComparisonHeadings
            .AsNoTracking()
            .AnyAsync(heading => heading.Id == headingId && heading.IsActive, cancellationToken);

    /// <summary>
    /// Whether the document exists, has not been deleted, and is visible to the caller.
    /// </summary>
    /// <remarks>
    /// The predicate names no company, deliberately — and the check is still company-scoped at
    /// runtime: the query runs as <c>app_api</c> with the caller's claims
    /// (<see cref="DatabaseSessionInterceptor"/>), and the <c>platform.document</c> policies filter by
    /// company. Another company's document is therefore indistinguishable from a missing one: the
    /// same 400, so the check cannot be used to probe for document ids. A test pins that
    /// indistinguishability; if the role or the policies ever changed, this predicate would be the
    /// thing to revisit.
    /// </remarks>
    public Task<bool> DocumentIsAvailableAsync(Guid documentId, CancellationToken cancellationToken) =>
        db.Documents
            .AsNoTracking()
            .AnyAsync(
                document => document.Id == documentId && document.DeletedAt == null,
                cancellationToken);

    public void Add(Statute statute) => db.Statutes.Add(statute);

    public void Add(StatuteArticle article) => db.StatuteArticles.Add(article);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        db.SaveChangesAsync(cancellationToken);
}
