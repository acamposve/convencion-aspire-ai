using Convenciones.Catalog;

namespace Convenciones.Negotiation;

/// <summary>
/// A union petition and its outcome (Q-5). Part of the <see cref="NegotiationCase"/> aggregate.
/// </summary>
/// <remarks>
/// The outcome graph is not documented by Q-5, which names the four outcomes only. The minimum graph
/// consistent with the evidence is implemented here: a petition starts <see cref="PetitionState.Open"/>,
/// may become <see cref="PetitionState.Agreed"/>, <see cref="PetitionState.Rejected"/> or
/// <see cref="PetitionState.Withdrawn"/>, and only an agreed petition can return to Open — which is
/// exactly the legacy "reopen petition" that cleared the old <c>9</c> (BR-12). Rejected and withdrawn are
/// terminal; nothing in the requirements or the legacy data reopens them.
/// </remarks>
public sealed class UnionPetition : IAuditable
{
    private UnionPetition()
    {
    }

    public Guid Id { get; private set; }

    public Guid CaseId { get; private set; }

    public int Number { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public Guid? HeadingId { get; private set; }

    public PetitionState State { get; private set; } = PetitionState.Open;

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    public List<EmployerOffer> Offers { get; private set; } = [];

    public List<MeetingAgreement> Agreements { get; private set; } = [];

    public string AuditModule => "negotiation";

    public string AuditEntityType => "union_petition";

    public string AuditEntityId => Id.ToString();

    internal static UnionPetition Create(
        Guid id,
        Guid caseId,
        int number,
        string? title,
        string? body,
        Guid? headingId,
        Guid actorId,
        DateTimeOffset now)
    {
        var petition = new UnionPetition
        {
            Id = id,
            CaseId = caseId,
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };

        petition.Apply(number, title, body, headingId, actorId, now);
        return petition;
    }

    internal void Update(
        int number,
        string? title,
        string? body,
        Guid? headingId,
        Guid actorId,
        DateTimeOffset now) =>
        Apply(number, title, body, headingId, actorId, now);

    /// <summary>Open → Agreed | Rejected | Withdrawn, and Agreed → Open. Everything else is refused.</summary>
    internal void ChangeState(PetitionState target, Guid actorId, DateTimeOffset now)
    {
        var allowed = (State, target) switch
        {
            (PetitionState.Open, PetitionState.Agreed) => true,
            (PetitionState.Open, PetitionState.Rejected) => true,
            (PetitionState.Open, PetitionState.Withdrawn) => true,
            (PetitionState.Agreed, PetitionState.Open) => true,
            _ => false,
        };

        if (!allowed)
        {
            throw NegotiationRuleException.Conflict(
                "illegal_petition_transition",
                $"A petition cannot move from '{State}' to '{target}'.");
        }

        State = target;
        UpdatedBy = actorId;
        UpdatedAt = now;
    }

    internal void EnsureOfferNumberAvailable(int number, Guid? exceptOfferId)
    {
        if (Offers.Any(offer => offer.Number == number && offer.Id != exceptOfferId))
        {
            throw NegotiationRuleException.Conflict(
                "duplicate_offer_number",
                $"Offer number {number} already exists in this petition.");
        }
    }

    private void Apply(
        int number,
        string? title,
        string? body,
        Guid? headingId,
        Guid actorId,
        DateTimeOffset now)
    {
        if (number < 1)
        {
            throw NegotiationRuleException.Validation("The petition number must be a positive integer.");
        }

        if (headingId == Guid.Empty)
        {
            throw NegotiationRuleException.Validation(
                "The comparison heading must be a real heading id, or omitted when unclassified.");
        }

        Number = number;
        Title = NegotiationText.Required(title, "The petition title is required.");
        Body = NegotiationText.Required(body, "The petition body is required.");
        HeadingId = headingId;
        UpdatedBy = actorId;
        UpdatedAt = now;
    }
}
