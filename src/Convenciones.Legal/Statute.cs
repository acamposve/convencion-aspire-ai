using Convenciones.Catalog;

namespace Convenciones.Legal;

/// <summary>
/// A statute: the aggregate root of the legal library, folding the legacy <c>ley_trabajo</c> (one
/// row, no primary key) and <c>otras_leyes</c> into one entity with a discriminator (Q-6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Company-independent by design.</b> A statute carries no <c>company_id</c>, and no tenant
/// predicate may be applied to it. Every active caller reads the same library; the RLS policy grants
/// read on <c>app.statute</c> and <c>app.statute_article</c> to any active user, without a company
/// test. This is the deliberate difference from agreements and it is asserted by tests, because
/// applying <c>TenantScope</c> here by reflex would make the library invisible to everyone.
/// </para>
/// <para>
/// <b>The single-labour-law invariant</b> allows at most one <see cref="StatuteKind.LabourLaw"/>
/// row. It spans rows, so no aggregate can evaluate it: the partial unique index
/// <c>statute_single_labour_law_key</c> is the guard, and the API translates its refusal into a 409
/// that names the index. That is deliberate, not a missing check: a pre-check in the handler would
/// race and would make the test unable to prove the index is what refuses the second row.
/// </para>
/// <para>
/// Article counts are computed from <see cref="Articles"/>, never stored. The legacy
/// <c>total_articulos</c> column was maintained by application code and silently drifted (DS-29).
/// </para>
/// </remarks>
public sealed class Statute : IAuditable
{
    private Statute()
    {
    }

    public Guid Id { get; private set; }

    public StatuteKind Kind { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateOnly? PublishedOn { get; private set; }

    /// <summary>Provenance of the statute text (gazette reference, URL) — the legacy <c>origen</c>.</summary>
    public string? Source { get; private set; }

    /// <summary>The statute's own file, if one was attached. Library-level, not tenant-level.</summary>
    public Guid? DocumentId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    public List<StatuteArticle> Articles { get; private set; } = [];

    public string AuditModule => "legal_library";

    public string AuditEntityType => "statute";

    public string AuditEntityId => Id.ToString();

    public bool IsLabourLaw => Kind is StatuteKind.LabourLaw;

    public int ArticleCount => Articles.Count;

    public static Statute Create(
        StatuteKind kind,
        string? name,
        DateOnly? publishedOn,
        string? source,
        Guid? documentId,
        Guid actorId,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(kind))
        {
            throw StatuteRuleException.Validation(
                "The statute kind must be 'LabourLaw' or 'OtherStatute'.");
        }

        var statute = new Statute
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };

        statute.ApplyHeader(name, publishedOn, source, documentId, actorId, now);
        return statute;
    }

    /// <summary>
    /// Changes the descriptive fields. <see cref="Kind"/> is deliberately not updatable: turning an
    /// <see cref="StatuteKind.OtherStatute"/> into the labour statute would have to obey the
    /// single-labour-law invariant, and that is a separate, explicit operation if it is ever needed.
    /// </summary>
    public void UpdateHeader(
        string? name,
        DateOnly? publishedOn,
        string? source,
        Guid? documentId,
        Guid actorId,
        DateTimeOffset now) =>
        ApplyHeader(name, publishedOn, source, documentId, actorId, now);

    public StatuteArticle AddArticle(
        int number,
        string? title,
        string? summary,
        string? body,
        Guid? headingId,
        string? economicComparisonNote,
        Guid actorId,
        DateTimeOffset now)
    {
        EnsureNumberAvailable(number, exceptArticleId: null);

        var article = StatuteArticle.Create(
            Guid.NewGuid(),
            Id,
            number,
            title,
            summary,
            body,
            headingId,
            economicComparisonNote,
            actorId,
            now);

        Articles.Add(article);
        return article;
    }

    public void EnsureNumberAvailable(int number, Guid? exceptArticleId)
    {
        if (Articles.Any(article => article.Number == number && article.Id != exceptArticleId))
        {
            throw StatuteRuleException.Conflict(
                "duplicate_article_number",
                $"Article number {number} already exists in this statute.");
        }
    }

    public StatuteArticle? FindArticle(Guid articleId) =>
        Articles.SingleOrDefault(article => article.Id == articleId);

    public void RemoveArticle(StatuteArticle article) => Articles.Remove(article);

    private void ApplyHeader(
        string? name,
        DateOnly? publishedOn,
        string? source,
        Guid? documentId,
        Guid actorId,
        DateTimeOffset now)
    {
        Name = StatuteText.Required(name, "The statute name is required.");
        PublishedOn = publishedOn;
        Source = StatuteText.Optional(source);
        DocumentId = documentId;
        UpdatedBy = actorId;
        UpdatedAt = now;
    }
}
