using Convenciones.Catalog;

namespace Convenciones.Agreements;

/// <summary>
/// A collective agreement: the aggregate root of the authoring workflow.
/// </summary>
/// <remarks>
/// <para>
/// The display title is <em>composed</em> from the company name and the validity period (Q-1). It is
/// deliberately not a property: the legacy stored <c>title</c> and <c>number</c> and both drifted
/// from the company and dates they claimed to describe.
/// </para>
/// <para>
/// Identity is <c>(CompanyId, ValidFrom)</c>. Uniqueness is enforced here and by the database index
/// <c>agreement_company_valid_from_key</c>; the API maps the constraint violation to 409.
/// </para>
/// </remarks>
public sealed class Agreement : IAuditable
{
    private Agreement()
    {
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public DateOnly ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public string Scope { get; private set; } = string.Empty;

    public PublicationState State { get; private set; } = PublicationState.Draft;

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    public List<AgreementClause> Clauses { get; private set; } = [];

    public List<AgreementAnnex> Annexes { get; private set; } = [];

    public string AuditModule => "agreements";

    public string AuditEntityType => "agreement";

    public string AuditEntityId => Id.ToString();

    public int ClauseCount => Clauses.Count;

    public static Agreement Create(
        Guid companyId,
        DateOnly validFrom,
        DateOnly? validTo,
        string? scope,
        Guid actorId,
        DateTimeOffset now)
    {
        if (companyId == Guid.Empty)
        {
            throw AgreementRuleException.Validation("An agreement must belong to a company.");
        }

        var agreement = new Agreement
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };

        agreement.ApplyHeader(validFrom, validTo, scope, actorId, now);
        return agreement;
    }

    /// <summary>
    /// Composed on read. The company name is supplied by the caller so the aggregate does not need a
    /// cross-module navigation, and so the title changes when the company name changes.
    /// </summary>
    public string ComposeTitle(string companyName) =>
        AgreementTitles.Compose(companyName, ValidFrom, ValidTo);

    public void UpdateHeader(
        DateOnly validFrom,
        DateOnly? validTo,
        string? scope,
        Guid actorId,
        DateTimeOffset now) =>
        ApplyHeader(validFrom, validTo, scope, actorId, now);

    public AgreementClause AddClause(
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
        EnsureNumberAvailable(number, exceptClauseId: null);

        var clause = AgreementClause.Create(
            Guid.NewGuid(),
            Id,
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

        Clauses.Add(clause);
        return clause;
    }

    public void EnsureNumberAvailable(int number, Guid? exceptClauseId)
    {
        if (Clauses.Any(clause => clause.Number == number && clause.Id != exceptClauseId))
        {
            throw AgreementRuleException.Conflict(
                "duplicate_clause_number",
                $"Clause number {number} already exists in this agreement.");
        }
    }

    public AgreementClause? FindClause(Guid clauseId) =>
        Clauses.SingleOrDefault(clause => clause.Id == clauseId);

    public void RemoveClause(AgreementClause clause)
    {
        if (State is PublicationState.Published && Clauses.Count == 1)
        {
            throw AgreementRuleException.InvalidState(
                "A published agreement must retain at least one clause.");
        }

        Clauses.Remove(clause);
    }

    public AgreementAnnex AddAnnex(
        string? name,
        string? description,
        string? body,
        Guid? documentId,
        int sortOrder,
        Guid actorId,
        DateTimeOffset now)
    {
        EnsureAnnexNameAvailable(name, exceptAnnexId: null);

        var annex = AgreementAnnex.Create(
            Guid.NewGuid(),
            Id,
            name,
            description,
            body,
            documentId,
            sortOrder,
            actorId,
            now);

        Annexes.Add(annex);
        return annex;
    }

    public void EnsureAnnexNameAvailable(string? name, Guid? exceptAnnexId)
    {
        var cleaned = name?.Trim();

        if (string.IsNullOrEmpty(cleaned))
        {
            return;
        }

        if (Annexes.Any(annex =>
                annex.Id != exceptAnnexId
                && string.Equals(annex.Name, cleaned, StringComparison.OrdinalIgnoreCase)))
        {
            throw AgreementRuleException.Conflict(
                "duplicate_annex_name",
                $"An annex named '{cleaned}' already exists in this agreement.");
        }
    }

    public AgreementAnnex? FindAnnex(Guid annexId) =>
        Annexes.SingleOrDefault(annex => annex.Id == annexId);

    public void RemoveAnnex(AgreementAnnex annex) => Annexes.Remove(annex);

    /// <summary>Draft → Published. Requires at least one clause and records the actor and time.</summary>
    public void Publish(Guid actorId, DateTimeOffset now)
    {
        if (State is PublicationState.Published)
        {
            throw AgreementRuleException.InvalidState("The agreement is already published.");
        }

        if (Clauses.Count == 0)
        {
            throw AgreementRuleException.InvalidState(
                "An agreement cannot be published without at least one clause.");
        }

        State = PublicationState.Published;
        PublishedAt = now;
        PublishedBy = actorId;
        UpdatedBy = actorId;
        UpdatedAt = now;
    }

    /// <summary>Published → Draft, clearing the recorded publication act.</summary>
    public void Unpublish(Guid actorId, DateTimeOffset now)
    {
        if (State is PublicationState.Draft)
        {
            throw AgreementRuleException.InvalidState("The agreement is not published.");
        }

        State = PublicationState.Draft;
        PublishedAt = null;
        PublishedBy = null;
        UpdatedBy = actorId;
        UpdatedAt = now;
    }

    private void ApplyHeader(
        DateOnly validFrom,
        DateOnly? validTo,
        string? scope,
        Guid actorId,
        DateTimeOffset now)
    {
        if (validFrom == default)
        {
            throw AgreementRuleException.Validation("The start date is required.");
        }

        if (validTo is { } end && end < validFrom)
        {
            throw AgreementRuleException.Validation("The end date cannot precede the start date.");
        }

        ValidFrom = validFrom;
        ValidTo = validTo;
        Scope = AgreementText.Required(scope, "The agreement scope is required.");
        UpdatedBy = actorId;
        UpdatedAt = now;
    }
}
