namespace Convenciones.Negotiation;

/// <summary>
/// Maps the aggregate to the response records, in one place so every route answers with the same shape.
/// </summary>
/// <remarks>
/// Enums are rendered by name, children are ordered deterministically, and no personal data beyond what
/// the case screen needs is included. <c>CompanyName</c> is deliberately absent: the client has the
/// company list, and the negotiation module does not need to join a module it does not own.
/// </remarks>
public static class NegotiationResponses
{
    public static CaseDetail Detail(NegotiationCase negotiationCase)
    {
        ArgumentNullException.ThrowIfNull(negotiationCase);

        return new CaseDetail(
            negotiationCase.Id,
            negotiationCase.CompanyId,
            negotiationCase.StartedOn,
            negotiationCase.State.ToString(),
            negotiationCase.ClosedAt,
            negotiationCase.PromotedAgreementId,
            negotiationCase.CreatedAt,
            negotiationCase.UpdatedAt,
            negotiationCase.Parties
                .OrderBy(party => party.Role)
                .ThenBy(party => party.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Party)
                .ToArray(),
            negotiationCase.Petitions
                .OrderBy(petition => petition.Number)
                .Select(Petition)
                .ToArray(),
            negotiationCase.Meetings
                .OrderByDescending(meeting => meeting.HeldOn)
                .ThenBy(meeting => meeting.Id)
                .Select(Meeting)
                .ToArray());
    }

    public static CaseSummary Summary(CaseSummaryRow row) =>
        new(
            row.Id,
            row.CompanyId,
            row.StartedOn,
            row.State.ToString(),
            row.ClosedAt,
            row.PromotedAgreementId,
            row.PartyCount,
            row.PetitionCount,
            row.MeetingCount,
            row.MeetingAgreementCount);

    public static PartyResponse Party(NegotiationParty party) =>
        new(party.Id, party.Role.ToString(), party.Name, party.Phone, party.Email, party.Position);

    public static PetitionResponse Petition(UnionPetition petition) =>
        new(
            petition.Id,
            petition.Number,
            petition.Title,
            petition.Body,
            petition.HeadingId,
            petition.State.ToString(),
            petition.Offers.OrderBy(offer => offer.Number).Select(Offer).ToArray(),
            petition.Agreements.OrderBy(agreement => agreement.Number).Select(MeetingAgreement).ToArray());

    public static OfferResponse Offer(EmployerOffer offer) =>
        new(offer.Id, offer.Number, offer.Title, offer.Body);

    public static MeetingResponse Meeting(NegotiationMeeting meeting) =>
        new(
            meeting.Id,
            meeting.HeldOn,
            meeting.StartTime,
            meeting.Duration,
            meeting.Summary,
            meeting.Details,
            meeting.Attendees);

    public static MeetingAgreementResponse MeetingAgreement(MeetingAgreement agreement) =>
        new(
            agreement.Id,
            agreement.PetitionId,
            agreement.MeetingId,
            agreement.AgreedOn,
            agreement.Number,
            agreement.Title,
            agreement.Body,
            agreement.HeadingId);
}

/// <summary>The list projection before the state is rendered; a SQL projection cannot call ToString.</summary>
public sealed record CaseSummaryRow(
    Guid Id,
    Guid CompanyId,
    DateOnly StartedOn,
    CaseState State,
    DateTimeOffset? ClosedAt,
    Guid? PromotedAgreementId,
    int PartyCount,
    int PetitionCount,
    int MeetingCount,
    int MeetingAgreementCount);
