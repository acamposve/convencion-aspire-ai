using Convenciones.Catalog;

namespace Convenciones.Agreements;

/// <summary>
/// One clause of a collective agreement. Part of the <see cref="Agreement"/> aggregate.
/// </summary>
/// <remarks>
/// The number is an integer, not a string: the legacy <c>varchar(11)</c> sorted as 1, 10, 100, 11, 2
/// and allowed duplicates (DS-23 / BR-02). Uniqueness is enforced by the aggregate and by the
/// database index <c>agreement_clause_number_key</c>.
/// </remarks>
public sealed class AgreementClause : IAuditable
{
    private AgreementClause()
    {
    }

    public Guid Id { get; private set; }

    public Guid AgreementId { get; private set; }

    public int Number { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Summary { get; private set; }

    public string Body { get; private set; } = string.Empty;

    /// <summary>Mandatory classification (Q-3/A-16). Never <see cref="Guid.Empty"/>.</summary>
    public Guid HeadingId { get; private set; }

    public string? EconomicComparisonNote { get; private set; }

    public bool IsExcludedFromCompilation { get; private set; }

    public string? ExclusionNote { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    public string AuditModule => "agreements";

    public string AuditEntityType => "agreement_clause";

    public string AuditEntityId => Id.ToString();

    public static AgreementClause Create(
        Guid id,
        Guid agreementId,
        int number,
        string? title,
        string? summary,
        string? body,
        Guid headingId,
        string? economicComparisonNote,
        bool isExcludedFromCompilation,
        string? exclusionNote,
        Guid actorId,
        DateTimeOffset now)
    {
        var clause = new AgreementClause
        {
            Id = id,
            AgreementId = agreementId,
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };

        clause.Apply(
            number,
            title,
            summary,
            body,
            headingId,
            economicComparisonNote,
            isExcludedFromCompilation,
            exclusionNote,
            actorId,
            now);

        return clause;
    }

    public void Update(
        int number,
        string? title,
        string? summary,
        string? body,
        Guid headingId,
        string? economicComparisonNote,
        bool isExcludedFromCompilation,
        string? exclusionNote,
        Guid actorId,
        DateTimeOffset now) =>
        Apply(
            number,
            title,
            summary,
            body,
            headingId,
            economicComparisonNote,
            isExcludedFromCompilation,
            exclusionNote,
            actorId,
            now);

    /// <summary>The only change allowed through <c>PUT .../heading</c>.</summary>
    public void Reclassify(Guid headingId, Guid actorId, DateTimeOffset now)
    {
        if (headingId == Guid.Empty)
        {
            throw AgreementRuleException.Validation("A clause must reference a comparison heading.");
        }

        HeadingId = headingId;
        UpdatedBy = actorId;
        UpdatedAt = now;
    }

    private void Apply(
        int number,
        string? title,
        string? summary,
        string? body,
        Guid headingId,
        string? economicComparisonNote,
        bool isExcludedFromCompilation,
        string? exclusionNote,
        Guid actorId,
        DateTimeOffset now)
    {
        if (number < 1)
        {
            throw AgreementRuleException.Validation("The clause number must be a positive integer.");
        }

        if (headingId == Guid.Empty)
        {
            throw AgreementRuleException.Validation("A clause must reference a comparison heading.");
        }

        var cleanExclusionNote = string.IsNullOrWhiteSpace(exclusionNote) ? null : exclusionNote.Trim();

        if (!isExcludedFromCompilation && cleanExclusionNote is not null)
        {
            throw AgreementRuleException.Validation(
                "An exclusion note is only allowed when the clause is excluded from compilation.");
        }

        Number = number;
        Title = AgreementText.Required(title, "The clause title is required.");
        Summary = AgreementText.Optional(summary);
        Body = AgreementText.Required(RichText.Clean(body), "The clause body is required.");
        HeadingId = headingId;
        EconomicComparisonNote = AgreementText.Optional(economicComparisonNote);
        IsExcludedFromCompilation = isExcludedFromCompilation;
        ExclusionNote = cleanExclusionNote;
        UpdatedBy = actorId;
        UpdatedAt = now;
    }
}
