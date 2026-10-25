namespace Convenciones.Negotiation;

/// <summary>
/// The five stages of a negotiation (Q-4). `Promoted` is deliberately absent: promotion is recorded by
/// <c>PromotedAgreementId</c> while the case rests in <see cref="Closed"/>.
/// </summary>
public enum CaseState
{
    Open = 0,
    InProgress = 1,
    Pending = 2,
    Agreed = 3,
    Closed = 4,
}

/// <summary>The outcome of a petition (Q-5), matching <c>app.petition_state</c>.</summary>
public enum PetitionState
{
    Open = 0,
    Agreed = 1,
    Rejected = 2,
    Withdrawn = 3,
}

/// <summary>The parties of a negotiation, matching <c>app.party_role</c>.</summary>
public enum PartyRole
{
    Employer = 0,
    Union = 1,
    LabourMinistry = 2,
    Other = 3,
}
