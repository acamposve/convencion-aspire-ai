namespace Convenciones.Negotiation;

/// <summary>Creates a negotiation case, directly in <see cref="CaseState.Open"/> (T11 D2).</summary>
public sealed class CreateCaseHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid companyId,
        CreateCaseRequest request,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        NegotiationCase negotiationCase;

        try
        {
            negotiationCase = NegotiationCase.Create(
                companyId, request.StartedOn, actorId, DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        store.Add(negotiationCase);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NegotiationCommandResult.CaseResult(
            NegotiationResponses.Detail(negotiationCase),
            NegotiationCommandStatus.Created);
    }
}

/// <summary>Adds a representative party to a case.</summary>
public sealed class AddPartyHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        PartyRequest request,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        if (!NegotiationEnumParsing.TryParsePartyRole(request.Role, out var role))
        {
            return NegotiationCommandResult.Validation(
                "The party role must be 'Employer', 'Union', 'LabourMinistry' or 'Other'.");
        }

        NegotiationParty party;

        try
        {
            party = negotiationCase.AddParty(
                role, request.Name, request.Phone, request.Email, request.Position, actorId, DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        store.Add(party);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NegotiationCommandResult.PartyCreated(NegotiationResponses.Party(party));
    }
}

public sealed class RemovePartyHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        Guid partyId,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        if (negotiationCase.Parties.All(party => party.Id != partyId))
        {
            return NegotiationCommandResult.NotFound($"No party with id '{partyId}' exists in this case.");
        }

        try
        {
            negotiationCase.RemoveParty(partyId, actorId, DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return NegotiationCommandResult.Deleted();
    }
}

public sealed class AddPetitionHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        PetitionRequest request,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        UnionPetition petition;

        try
        {
            petition = negotiationCase.AddPetition(
                request.Number,
                request.Title,
                request.Body,
                request.HeadingId,
                actorId,
                DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        store.Add(petition);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NegotiationCommandResult.PetitionCreated(NegotiationResponses.Petition(petition));
    }
}

public sealed class UpdatePetitionHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        Guid petitionId,
        PetitionRequest request,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        var petition = negotiationCase.Petitions.SingleOrDefault(candidate => candidate.Id == petitionId);

        if (petition is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingPetition(petitionId));
        }

        try
        {
            negotiationCase.UpdatePetition(
                petitionId,
                request.Number,
                request.Title,
                request.Body,
                request.HeadingId,
                actorId,
                DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NegotiationCommandResult.PetitionChanged(NegotiationResponses.Petition(petition));
    }
}

public sealed class DeletePetitionHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        Guid petitionId,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        if (negotiationCase.Petitions.All(petition => petition.Id != petitionId))
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingPetition(petitionId));
        }

        try
        {
            negotiationCase.DeletePetition(petitionId, actorId, DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return NegotiationCommandResult.Deleted();
    }
}

/// <summary>
/// Changes a petition's outcome (Q-5). Only the transitions documented by the minimum graph are
/// accepted: Open → Agreed | Rejected | Withdrawn, and Agreed → Open (the legacy "reopen petition").
/// </summary>
public sealed class ChangePetitionStateHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        Guid petitionId,
        PetitionStateRequest request,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        var petition = negotiationCase.Petitions.SingleOrDefault(candidate => candidate.Id == petitionId);

        if (petition is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingPetition(petitionId));
        }

        if (!NegotiationEnumParsing.TryParsePetitionState(request.State, out var target))
        {
            return NegotiationCommandResult.Validation(
                "The petition state must be 'Open', 'Agreed', 'Rejected' or 'Withdrawn'.");
        }

        try
        {
            negotiationCase.ChangePetitionState(petitionId, target, actorId, DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NegotiationCommandResult.PetitionChanged(NegotiationResponses.Petition(petition));
    }
}

public sealed class AddOfferHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        Guid petitionId,
        OfferRequest request,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        if (negotiationCase.Petitions.All(petition => petition.Id != petitionId))
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingPetition(petitionId));
        }

        EmployerOffer offer;

        try
        {
            offer = negotiationCase.AddOffer(
                petitionId, request.Number, request.Title, request.Body, actorId, DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        store.Add(offer);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NegotiationCommandResult.OfferCreated(NegotiationResponses.Offer(offer));
    }
}

public sealed class DeleteOfferHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        Guid petitionId,
        Guid offerId,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        var petition = negotiationCase.Petitions.SingleOrDefault(candidate => candidate.Id == petitionId);

        if (petition is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingPetition(petitionId));
        }

        if (petition.Offers.All(offer => offer.Id != offerId))
        {
            return NegotiationCommandResult.NotFound($"No offer with id '{offerId}' exists in this petition.");
        }

        try
        {
            negotiationCase.DeleteOffer(petitionId, offerId, actorId, DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return NegotiationCommandResult.Deleted();
    }
}

public sealed class AddMeetingHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        MeetingRequest request,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        NegotiationMeeting meeting;

        try
        {
            meeting = negotiationCase.AddMeeting(
                request.HeldOn,
                request.StartTime,
                request.Duration,
                request.Summary,
                request.Details,
                request.Attendees,
                actorId,
                DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        store.Add(meeting);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NegotiationCommandResult.MeetingCreated(NegotiationResponses.Meeting(meeting));
    }
}

public sealed class DeleteMeetingHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        Guid meetingId,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        if (negotiationCase.Meetings.All(meeting => meeting.Id != meetingId))
        {
            return NegotiationCommandResult.NotFound($"No meeting with id '{meetingId}' exists in this case.");
        }

        try
        {
            negotiationCase.DeleteMeeting(meetingId, actorId, DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return NegotiationCommandResult.Deleted();
    }
}

public sealed class AddMeetingAgreementHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        Guid petitionId,
        MeetingAgreementRequest request,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        if (negotiationCase.Petitions.All(petition => petition.Id != petitionId))
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingPetition(petitionId));
        }

        MeetingAgreement agreement;

        try
        {
            agreement = negotiationCase.AddMeetingAgreement(
                petitionId,
                request.MeetingId,
                request.AgreedOn,
                request.Number,
                request.Title,
                request.Body,
                request.HeadingId,
                actorId,
                DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        store.Add(agreement);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NegotiationCommandResult.MeetingAgreementCreated(
            NegotiationResponses.MeetingAgreement(agreement));
    }
}

public sealed class DeleteMeetingAgreementHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        Guid petitionId,
        Guid agreementId,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        var petition = negotiationCase.Petitions.SingleOrDefault(candidate => candidate.Id == petitionId);

        if (petition is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingPetition(petitionId));
        }

        if (petition.Agreements.All(agreement => agreement.Id != agreementId))
        {
            return NegotiationCommandResult.NotFound(
                $"No meeting agreement with id '{agreementId}' exists in this petition.");
        }

        try
        {
            negotiationCase.DeleteMeetingAgreement(petitionId, agreementId, actorId, DateTimeOffset.UtcNow);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return NegotiationCommandResult.Deleted();
    }
}

/// <summary>The five state transitions that are not promotion (T11 D2/D3).</summary>
public enum CaseTransition
{
    StartInProgress,
    MarkPending,
    Agree,
    Close,
    Reopen,
}

/// <summary>Moves a case through the state machine and returns the case as it stands afterwards.</summary>
public sealed class TransitionCaseHandler(INegotiationStore store)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        CaseTransition transition,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        try
        {
            var now = DateTimeOffset.UtcNow;

            switch (transition)
            {
                case CaseTransition.StartInProgress:
                    negotiationCase.StartInProgress(actorId, now);
                    break;
                case CaseTransition.MarkPending:
                    negotiationCase.MarkPending(actorId, now);
                    break;
                case CaseTransition.Agree:
                    negotiationCase.Agree(actorId, now);
                    break;
                case CaseTransition.Close:
                    negotiationCase.Close(actorId, now);
                    break;
                case CaseTransition.Reopen:
                    negotiationCase.Reopen(actorId, now);
                    break;
                default:
                    return NegotiationCommandResult.Validation("Unknown case transition.");
            }
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NegotiationCommandResult.CaseResult(NegotiationResponses.Detail(negotiationCase));
    }
}

/// <summary>
/// Promotes a closed case into an agreement (T11 D4, D5).
/// </summary>
/// <remarks>
/// <para>
/// The agreement is created through the agreement module's factory and attached to the same
/// <c>DbContext</c>; the case link is set on the tracked aggregate; one <c>SaveChanges</c> commits both
/// in one transaction. The database guard repeats the state, capability and "already promoted" rules, so
/// a concurrent promotion cannot produce a second agreement (T11 AC 2, 3, 6).
/// </para>
/// <para>
/// Only the agreement header is created: meeting agreements are the negotiation record and are not
/// copied into clauses in the MVP (D5). The clause text is authored in the agreement workspace.
/// </para>
/// </remarks>
public sealed class PromoteCaseHandler(INegotiationStore store, IPromotedAgreementFactory agreementFactory)
{
    public async Task<NegotiationCommandResult> HandleAsync(
        Guid caseId,
        PromoteCaseRequest request,
        Guid? visibleCompanyId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var negotiationCase = await store.FindAsync(caseId, visibleCompanyId, cancellationToken)
            .ConfigureAwait(false);

        if (negotiationCase is null)
        {
            return NegotiationCommandResult.NotFound(NegotiationMessages.MissingCase(caseId));
        }

        if (request.ValidFrom == default)
        {
            return NegotiationCommandResult.Validation("The agreement start date is required.");
        }

        // Legacy wrote "*Inicio"/"*Fin"/"*Ambito" placeholders. The dates are typed and cannot carry
        // them; the scope is free text and is checked here (T11 D4).
        if (!string.IsNullOrWhiteSpace(request.Scope)
            && request.Scope.TrimStart().StartsWith('*'))
        {
            return NegotiationCommandResult.Validation(
                "The scope looks like a legacy placeholder. Provide a real scope.");
        }

        if (negotiationCase.State is not CaseState.Closed)
        {
            return NegotiationCommandResult.InvalidState(
                negotiationCase.State,
                $"The case is '{negotiationCase.State}'. Only a closed case can be promoted.");
        }

        if (negotiationCase.IsPromoted)
        {
            return NegotiationCommandResult.Conflict(
                $"The case was already promoted to agreement '{negotiationCase.PromotedAgreementId}'.");
        }

        var now = DateTimeOffset.UtcNow;

        // Attaches the agreement to the unit of work; the factory applies the agreement aggregate's own
        // validation, so promotion cannot drift from POST /agreements.
        var agreementId = agreementFactory.CreateAndAttach(
            negotiationCase.CompanyId,
            request.ValidFrom,
            request.ValidTo,
            request.Scope,
            actorId,
            now);

        try
        {
            negotiationCase.Promote(agreementId, actorId, now);
        }
        catch (NegotiationRuleException rule)
        {
            return NegotiationCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new NegotiationCommandResult(
            NegotiationCommandStatus.Ok,
            AgreementId: agreementId,
            Case: NegotiationResponses.Detail(negotiationCase));
    }

}

internal static class NegotiationMessages
{
    public static string MissingCase(Guid caseId) =>
        $"No negotiation case with id '{caseId}' exists.";

    public static string MissingPetition(Guid petitionId) =>
        $"No petition with id '{petitionId}' exists in this case.";
}
