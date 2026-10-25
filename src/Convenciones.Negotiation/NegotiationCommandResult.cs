namespace Convenciones.Negotiation;

/// <summary>The outcome of a negotiation command, in domain terms rather than HTTP terms.</summary>
public enum NegotiationCommandStatus
{
    Ok,
    Created,
    NoContent,
    NotFound,
    ValidationFailed,
    Conflict,
    InvalidState,
    ConcurrentModification,
}

/// <summary>
/// What a command answers: the shape depends on the use case, and the endpoint decides the status.
/// </summary>
/// <remarks>
/// <see cref="State"/> carries the current case state on an invalid-state refusal, because T11 requires
/// the response to name it. <see cref="AgreementId"/> is what promotion answers.
/// </remarks>
public sealed record NegotiationCommandResult(
    NegotiationCommandStatus Status,
    string? Detail = null,
    CaseDetail? Case = null,
    PartyResponse? Party = null,
    PetitionResponse? Petition = null,
    OfferResponse? Offer = null,
    MeetingResponse? Meeting = null,
    MeetingAgreementResponse? MeetingAgreement = null,
    CaseState? State = null,
    Guid? AgreementId = null)
{
    public static NegotiationCommandResult CaseResult(
        CaseDetail detail,
        NegotiationCommandStatus status = NegotiationCommandStatus.Ok) =>
        new(status, Case: detail);

    public static NegotiationCommandResult PartyCreated(PartyResponse party) =>
        new(NegotiationCommandStatus.Created, Party: party);

    public static NegotiationCommandResult PetitionCreated(PetitionResponse petition) =>
        new(NegotiationCommandStatus.Created, Petition: petition);

    public static NegotiationCommandResult PetitionChanged(PetitionResponse petition) =>
        new(NegotiationCommandStatus.Ok, Petition: petition);

    public static NegotiationCommandResult OfferCreated(OfferResponse offer) =>
        new(NegotiationCommandStatus.Created, Offer: offer);

    public static NegotiationCommandResult MeetingCreated(MeetingResponse meeting) =>
        new(NegotiationCommandStatus.Created, Meeting: meeting);

    public static NegotiationCommandResult MeetingAgreementCreated(MeetingAgreementResponse agreement) =>
        new(NegotiationCommandStatus.Created, MeetingAgreement: agreement);

    public static NegotiationCommandResult Promoted(Guid agreementId, Guid caseId) =>
        new(NegotiationCommandStatus.Ok, Detail: null, AgreementId: agreementId);

    public static NegotiationCommandResult Deleted() => new(NegotiationCommandStatus.NoContent);

    public static NegotiationCommandResult NotFound(string detail) =>
        new(NegotiationCommandStatus.NotFound, detail);

    public static NegotiationCommandResult Validation(string detail) =>
        new(NegotiationCommandStatus.ValidationFailed, detail);

    public static NegotiationCommandResult Conflict(string detail) =>
        new(NegotiationCommandStatus.Conflict, detail);

    public static NegotiationCommandResult InvalidState(CaseState state, string detail) =>
        new(NegotiationCommandStatus.InvalidState, detail, State: state);

    public static NegotiationCommandResult ConcurrentModification() =>
        new(
            NegotiationCommandStatus.ConcurrentModification,
            "The case was modified by someone else. Re-read it and retry.");

    internal static NegotiationCommandResult FromRule(NegotiationRuleException rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Kind switch
        {
            NegotiationRuleKind.Validation => Validation(rule.Message),
            NegotiationRuleKind.Conflict => Conflict(rule.Message),
            _ => InvalidState(rule.CurrentState ?? CaseState.Open, rule.Message),
        };
    }
}
