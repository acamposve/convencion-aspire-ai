using Convenciones.Catalog;

namespace Convenciones.Negotiation;

/// <summary>
/// A negotiation for one company: the aggregate root of the workflow.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle is <c>Open → InProgress → Pending → Agreed → Closed</c> (Q-4). A new case is created
/// directly in <see cref="CaseState.Open"/> — Q-4 removed the old <c>Draft</c>, so there is no state
/// before Open and no "open" operation. Promotion is a separate operation available only for a
/// <see cref="CaseState.Closed"/> case; it records <see cref="PromotedAgreementId"/> and leaves the
/// state Closed (Q-4, ADR-014).
/// </para>
/// <para>
/// A closed case is read-only: every child mutation goes through <see cref="EnsureEditable"/>, and the
/// only way out is <see cref="Reopen"/> (T11 D7). A promoted case is terminal — reopening it is
/// refused, the link is never cleared here, and the database guard refuses the same statement even if
/// this method is bypassed (A-21, ADR-014).
/// </para>
/// </remarks>
public sealed class NegotiationCase : IAuditable
{
    private NegotiationCase()
    {
    }

    public Guid Id { get; private set; }

    public Guid CompanyId { get; private set; }

    public DateOnly StartedOn { get; private set; }

    public CaseState State { get; private set; } = CaseState.Open;

    public DateTimeOffset? ClosedAt { get; private set; }

    public Guid? PromotedAgreementId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    public List<NegotiationParty> Parties { get; private set; } = [];

    public List<UnionPetition> Petitions { get; private set; } = [];

    public List<NegotiationMeeting> Meetings { get; private set; } = [];

    public string AuditModule => "negotiation";

    public string AuditEntityType => "negotiation_case";

    public string AuditEntityId => Id.ToString();

    public bool IsPromoted => PromotedAgreementId is not null;

    public int MeetingAgreementCount => Petitions.Sum(petition => petition.Agreements.Count);

    public static NegotiationCase Create(
        Guid companyId,
        DateOnly startedOn,
        Guid actorId,
        DateTimeOffset now)
    {
        if (companyId == Guid.Empty)
        {
            throw NegotiationRuleException.Validation("A negotiation case must belong to a company.");
        }

        if (startedOn == default)
        {
            throw NegotiationRuleException.Validation("The start date is required.");
        }

        return new NegotiationCase
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            StartedOn = startedOn,
            State = CaseState.Open,
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };
    }

    /// <summary>A closed case is read-only; reopen it first (T11 D7).</summary>
    public void EnsureEditable()
    {
        if (State is CaseState.Closed)
        {
            throw NegotiationRuleException.InvalidState(
                State,
                "The case is closed. Reopen it before changing parties, petitions, offers, meetings or agreements.");
        }
    }

    // ---- Parties ---------------------------------------------------------------------------------

    public NegotiationParty AddParty(
        PartyRole role,
        string? name,
        string? phone,
        string? email,
        string? position,
        Guid actorId,
        DateTimeOffset now)
    {
        EnsureEditable();

        var party = NegotiationParty.Create(Guid.NewGuid(), Id, role, name, phone, email, position, now);
        Parties.Add(party);
        Touch(actorId, now);
        return party;
    }

    public void RemoveParty(Guid partyId, Guid actorId, DateTimeOffset now)
    {
        EnsureEditable();

        var party = Parties.SingleOrDefault(candidate => candidate.Id == partyId)
            ?? throw NegotiationRuleException.Validation("The party does not exist in this case.");

        Parties.Remove(party);
        Touch(actorId, now);
    }

    // ---- Petitions -------------------------------------------------------------------------------

    public UnionPetition AddPetition(
        int number,
        string? title,
        string? body,
        Guid? headingId,
        Guid actorId,
        DateTimeOffset now)
    {
        EnsureEditable();
        EnsurePetitionNumberAvailable(number, exceptPetitionId: null);

        var petition = UnionPetition.Create(
            Guid.NewGuid(), Id, number, title, body, headingId, actorId, now);

        Petitions.Add(petition);
        Touch(actorId, now);
        return petition;
    }

    public void UpdatePetition(
        Guid petitionId,
        int number,
        string? title,
        string? body,
        Guid? headingId,
        Guid actorId,
        DateTimeOffset now)
    {
        EnsureEditable();

        var petition = FindPetition(petitionId);

        if (petition.State is PetitionState.Agreed)
        {
            throw NegotiationRuleException.Conflict(
                "petition_agreed",
                "An agreed petition cannot be edited or deleted (INV-N3). Set it back to Open first.");
        }

        EnsurePetitionNumberAvailable(number, petition.Id);
        petition.Update(number, title, body, headingId, actorId, now);
        Touch(actorId, now);
    }

    public void DeletePetition(Guid petitionId, Guid actorId, DateTimeOffset now)
    {
        EnsureEditable();

        var petition = FindPetition(petitionId);

        if (petition.State is PetitionState.Agreed)
        {
            throw NegotiationRuleException.Conflict(
                "petition_agreed",
                "An agreed petition cannot be edited or deleted (INV-N3). Set it back to Open first.");
        }

        Petitions.Remove(petition);
        Touch(actorId, now);
    }

    public void ChangePetitionState(
        Guid petitionId,
        PetitionState target,
        Guid actorId,
        DateTimeOffset now)
    {
        EnsureEditable();

        var petition = FindPetition(petitionId);
        petition.ChangeState(target, actorId, now);
        Touch(actorId, now);
    }

    public UnionPetition FindPetition(Guid petitionId) =>
        Petitions.SingleOrDefault(petition => petition.Id == petitionId)
        ?? throw NegotiationRuleException.Validation("The petition does not exist in this case.");

    public void EnsurePetitionNumberAvailable(int number, Guid? exceptPetitionId)
    {
        if (Petitions.Any(petition => petition.Number == number && petition.Id != exceptPetitionId))
        {
            throw NegotiationRuleException.Conflict(
                "duplicate_petition_number",
                $"Petition number {number} already exists in this case.");
        }
    }

    // ---- Offers ----------------------------------------------------------------------------------

    public EmployerOffer AddOffer(
        Guid petitionId,
        int number,
        string? title,
        string? body,
        Guid actorId,
        DateTimeOffset now)
    {
        EnsureEditable();

        var petition = FindPetition(petitionId);
        petition.EnsureOfferNumberAvailable(number, exceptOfferId: null);

        var offer = EmployerOffer.Create(Guid.NewGuid(), petition.Id, number, title, body, actorId, now);
        petition.Offers.Add(offer);
        Touch(actorId, now);
        return offer;
    }

    public void DeleteOffer(Guid petitionId, Guid offerId, Guid actorId, DateTimeOffset now)
    {
        EnsureEditable();

        var petition = FindPetition(petitionId);
        var offer = petition.Offers.SingleOrDefault(candidate => candidate.Id == offerId)
            ?? throw NegotiationRuleException.Validation("The offer does not exist in this petition.");

        petition.Offers.Remove(offer);
        Touch(actorId, now);
    }

    // ---- Meetings --------------------------------------------------------------------------------

    public NegotiationMeeting AddMeeting(
        DateOnly heldOn,
        TimeOnly? startTime,
        string? duration,
        string? summary,
        string? details,
        string? attendees,
        Guid actorId,
        DateTimeOffset now)
    {
        EnsureEditable();

        var meeting = NegotiationMeeting.Create(
            Guid.NewGuid(), Id, heldOn, startTime, duration, summary, details, attendees, actorId, now);

        Meetings.Add(meeting);
        Touch(actorId, now);
        return meeting;
    }

    public void DeleteMeeting(Guid meetingId, Guid actorId, DateTimeOffset now)
    {
        EnsureEditable();

        var meeting = Meetings.SingleOrDefault(candidate => candidate.Id == meetingId)
            ?? throw NegotiationRuleException.Validation("The meeting does not exist in this case.");

        if (Petitions.SelectMany(petition => petition.Agreements).Any(agreement => agreement.MeetingId == meeting.Id))
        {
            throw NegotiationRuleException.Conflict(
                "meeting_has_agreements",
                "The meeting has agreements recorded against it. Remove them before deleting the meeting.");
        }

        Meetings.Remove(meeting);
        Touch(actorId, now);
    }

    // ---- Meeting agreements ----------------------------------------------------------------------

    public MeetingAgreement AddMeetingAgreement(
        Guid petitionId,
        Guid meetingId,
        DateOnly agreedOn,
        int number,
        string? title,
        string? body,
        Guid? headingId,
        Guid actorId,
        DateTimeOffset now)
    {
        EnsureEditable();

        var petition = FindPetition(petitionId);

        if (!Meetings.Any(meeting => meeting.Id == meetingId))
        {
            throw NegotiationRuleException.Validation("The meeting does not belong to this case.");
        }

        EnsureMeetingAgreementNumberAvailable(number, exceptAgreementId: null);

        var agreement = MeetingAgreement.Create(
            Guid.NewGuid(), Id, petition.Id, meetingId, agreedOn, number, title, body, headingId, actorId, now);

        petition.Agreements.Add(agreement);
        Touch(actorId, now);
        return agreement;
    }

    public void DeleteMeetingAgreement(
        Guid petitionId,
        Guid agreementId,
        Guid actorId,
        DateTimeOffset now)
    {
        EnsureEditable();

        var petition = FindPetition(petitionId);
        var agreement = petition.Agreements.SingleOrDefault(candidate => candidate.Id == agreementId)
            ?? throw NegotiationRuleException.Validation("The agreement does not exist in this petition.");

        petition.Agreements.Remove(agreement);
        Touch(actorId, now);
    }

    public void EnsureMeetingAgreementNumberAvailable(int number, Guid? exceptAgreementId)
    {
        if (Petitions
            .SelectMany(petition => petition.Agreements)
            .Any(agreement => agreement.Number == number && agreement.Id != exceptAgreementId))
        {
            throw NegotiationRuleException.Conflict(
                "duplicate_meeting_agreement_number",
                $"Meeting agreement number {number} already exists in this case.");
        }
    }

    // ---- Transitions -----------------------------------------------------------------------------

    public void StartInProgress(Guid actorId, DateTimeOffset now)
    {
        if (State is not (CaseState.Open or CaseState.Pending))
        {
            throw NegotiationRuleException.InvalidState(
                State,
                $"The case is '{State}'. It can only move to InProgress from Open or Pending.");
        }

        State = CaseState.InProgress;
        Touch(actorId, now);
    }

    public void MarkPending(Guid actorId, DateTimeOffset now)
    {
        if (State is not CaseState.InProgress)
        {
            throw NegotiationRuleException.InvalidState(
                State,
                $"The case is '{State}'. It can only move to Pending from InProgress.");
        }

        State = CaseState.Pending;
        Touch(actorId, now);
    }

    public void Agree(Guid actorId, DateTimeOffset now)
    {
        if (State is not CaseState.Pending)
        {
            throw NegotiationRuleException.InvalidState(
                State,
                $"The case is '{State}'. It can only move to Agreed from Pending.");
        }

        State = CaseState.Agreed;
        Touch(actorId, now);
    }

    /// <summary>
    /// Agreed → Closed. Requires at least one meeting agreement: the API contract names "close without
    /// agreements" as an invalid-state case, and INV-N2 states the same rule.
    /// </summary>
    public void Close(Guid actorId, DateTimeOffset now)
    {
        if (State is not CaseState.Agreed)
        {
            throw NegotiationRuleException.InvalidState(
                State,
                $"The case is '{State}'. It can only be closed from Agreed.");
        }

        if (MeetingAgreementCount == 0)
        {
            throw NegotiationRuleException.InvalidState(
                State,
                "The case has no meeting agreements. At least one is required before closing.");
        }

        State = CaseState.Closed;
        ClosedAt = now;
        Touch(actorId, now);
    }

    /// <summary>
    /// Closed → InProgress. Refused once the case is promoted: promotion is terminal (A-21, ADR-014),
    /// and the database CHECK would refuse the resulting row as well.
    /// </summary>
    public void Reopen(Guid actorId, DateTimeOffset now)
    {
        if (State is not CaseState.Closed)
        {
            throw NegotiationRuleException.InvalidState(
                State,
                $"The case is '{State}'. It can only be reopened from Closed.");
        }

        if (IsPromoted)
        {
            throw NegotiationRuleException.InvalidState(
                State,
                "The case was promoted to an agreement and is terminal; it cannot be reopened (A-21).");
        }

        State = CaseState.InProgress;
        ClosedAt = null;
        Touch(actorId, now);
    }

    /// <summary>
    /// Records the agreement produced by a closed case. The state stays Closed; the link is the
    /// promotion. The database guard repeats every rule here independently (T11 AC 2, 4, 6).
    /// </summary>
    public void Promote(Guid agreementId, Guid actorId, DateTimeOffset now)
    {
        if (agreementId == Guid.Empty)
        {
            throw NegotiationRuleException.Validation("The promoted agreement id is required.");
        }

        if (State is not CaseState.Closed)
        {
            throw NegotiationRuleException.InvalidState(
                State,
                $"The case is '{State}'. Only a closed case can be promoted.");
        }

        if (IsPromoted)
        {
            throw NegotiationRuleException.Conflict(
                "already_promoted",
                $"The case was already promoted to agreement '{PromotedAgreementId}'.");
        }

        PromotedAgreementId = agreementId;
        Touch(actorId, now);
    }

    private void Touch(Guid actorId, DateTimeOffset now)
    {
        UpdatedBy = actorId;
        UpdatedAt = now;
    }
}
