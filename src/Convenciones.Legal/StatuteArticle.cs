using Convenciones.Catalog;

namespace Convenciones.Legal;

/// <summary>
/// One article of a statute. Part of the <see cref="Statute"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// The number is an integer, not a string: the legacy article tables typed the same concept as
/// <c>int</c> in one table and <c>varchar(11)</c> in the other, so ordering produced 1, 10, 100, 11,
/// 2 (DS-23). Uniqueness is <c>(statute_id, number_int)</c>, enforced by the aggregate and by the
/// database index <c>statute_article_number_key</c>.
/// </para>
/// <para>
/// <see cref="HeadingId"/> is <b>nullable</b>, unlike <c>AgreementClause.HeadingId</c>. Approval item
/// A-12: 89.6% of legacy other-statute articles carried no comparison heading, so requiring
/// classification would block loading the library at all. <see cref="Title"/> is a different field
/// and <b>is</b> mandatory — an article with no heading of its own is stored text with no citation.
/// </para>
/// </remarks>
public sealed class StatuteArticle : IAuditable
{
    private StatuteArticle()
    {
    }

    public Guid Id { get; private set; }

    public Guid StatuteId { get; private set; }

    public int Number { get; private set; }

    /// <summary>The article's own heading, e.g. "Artículo 12 — Vacaciones". Mandatory.</summary>
    public string Title { get; private set; } = string.Empty;

    public string? Summary { get; private set; }

    public string Body { get; private set; } = string.Empty;

    /// <summary>Optional comparison classification (A-12). <see langword="null"/> when unclassified.</summary>
    public Guid? HeadingId { get; private set; }

    public string? EconomicComparisonNote { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    public string AuditModule => "legal_library";

    public string AuditEntityType => "statute_article";

    public string AuditEntityId => Id.ToString();

    internal static StatuteArticle Create(
        Guid id,
        Guid statuteId,
        int number,
        string? title,
        string? summary,
        string? body,
        Guid? headingId,
        string? economicComparisonNote,
        Guid actorId,
        DateTimeOffset now)
    {
        var article = new StatuteArticle
        {
            Id = id,
            StatuteId = statuteId,
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };

        article.Apply(number, title, summary, body, headingId, economicComparisonNote, actorId, now);
        return article;
    }

    public void Update(
        int number,
        string? title,
        string? summary,
        string? body,
        Guid? headingId,
        string? economicComparisonNote,
        Guid actorId,
        DateTimeOffset now) =>
        Apply(number, title, summary, body, headingId, economicComparisonNote, actorId, now);

    private void Apply(
        int number,
        string? title,
        string? summary,
        string? body,
        Guid? headingId,
        string? economicComparisonNote,
        Guid actorId,
        DateTimeOffset now)
    {
        if (number < 1)
        {
            throw StatuteRuleException.Validation("The article number must be a positive integer.");
        }

        // A nullable classification is an explicit "unclassified" (A-12). An empty Guid is not a
        // classification at all: it is the client failing to send null, and storing it would create a
        // heading reference that can never resolve.
        if (headingId == Guid.Empty)
        {
            throw StatuteRuleException.Validation(
                "The comparison heading must be a real heading id, or omitted when the article is "
                + "unclassified.");
        }

        Number = number;
        Title = StatuteText.Required(
            title,
            "The article heading is required: an article with no heading cannot be cited.");
        Summary = StatuteText.Optional(summary);
        Body = StatuteText.Required(body, "The article body is required.");
        HeadingId = headingId;
        EconomicComparisonNote = StatuteText.Optional(economicComparisonNote);
        UpdatedBy = actorId;
        UpdatedAt = now;
    }
}
