using System;
using Convenciones.Negotiation;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests;

/// <summary>
/// The negotiation state machine, exercised on the aggregate without a server or a database.
/// </summary>
/// <remarks>
/// Every rule here is repeated by the database (the two consistency CHECKs and the promotion guard), and
/// the integration tests assert the database half. These tests exist so the legal and illegal
/// transitions are enumerated in one readable place and a change to the chain fails loudly.
/// </remarks>
public sealed class NegotiationStateMachineTests
{
    private static readonly Guid Actor = new("d1000000-0000-0000-0000-000000000001");

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid Heading = new("d2000000-0000-0000-0000-000000000001");

    [Fact]
    public void A_new_case_starts_open()
    {
        var negotiationCase = NewCase();

        negotiationCase.State.ShouldBe(CaseState.Open);
        negotiationCase.ClosedAt.ShouldBeNull();
        negotiationCase.PromotedAgreementId.ShouldBeNull();
    }

    [Fact]
    public void The_documented_chain_can_be_walked_and_close_records_the_timestamp()
    {
        var negotiationCase = NewCase();
        var petition = negotiationCase.AddPetition(1, "Salarios", "Texto", null, Actor, Now);
        var meeting = negotiationCase.AddMeeting(new DateOnly(2026, 1, 5), null, null, null, null, null, Actor, Now);
        negotiationCase.AddMeetingAgreement(
            petition.Id, meeting.Id, new DateOnly(2026, 1, 5), 1, "Salarios", "Acuerdo", null, Actor, Now);

        negotiationCase.StartInProgress(Actor, Now);
        negotiationCase.State.ShouldBe(CaseState.InProgress);

        negotiationCase.MarkPending(Actor, Now);
        negotiationCase.State.ShouldBe(CaseState.Pending);

        negotiationCase.Agree(Actor, Now);
        negotiationCase.State.ShouldBe(CaseState.Agreed);

        negotiationCase.Close(Actor, Now);
        negotiationCase.State.ShouldBe(CaseState.Closed);
        negotiationCase.ClosedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(CaseState.Open)]
    [InlineData(CaseState.InProgress)]
    [InlineData(CaseState.Pending)]
    public void Closing_is_only_allowed_from_agreed(CaseState from)
    {
        var negotiationCase = CaseIn(from);

        var exception = Should.Throw<NegotiationRuleException>(() => negotiationCase.Close(Actor, Now));

        exception.Kind.ShouldBe(NegotiationRuleKind.InvalidState);
        exception.CurrentState.ShouldBe(from);
    }

    [Fact]
    public void Closing_requires_at_least_one_meeting_agreement()
    {
        var negotiationCase = CaseIn(CaseState.Agreed);

        var exception = Should.Throw<NegotiationRuleException>(() => negotiationCase.Close(Actor, Now));

        exception.Kind.ShouldBe(NegotiationRuleKind.InvalidState);
        exception.Message.ShouldContain("meeting agreement");
        negotiationCase.State.ShouldBe(CaseState.Agreed);
    }

    [Fact]
    public void Agreeing_is_only_allowed_from_pending()
    {
        var negotiationCase = CaseIn(CaseState.InProgress);

        Should.Throw<NegotiationRuleException>(() => negotiationCase.Agree(Actor, Now))
            .CurrentState.ShouldBe(CaseState.InProgress);
    }

    [Fact]
    public void Pending_is_only_entered_from_in_progress()
    {
        var negotiationCase = CaseIn(CaseState.Open);

        Should.Throw<NegotiationRuleException>(() => negotiationCase.MarkPending(Actor, Now))
            .Kind.ShouldBe(NegotiationRuleKind.InvalidState);
    }

    [Fact]
    public void In_progress_can_be_entered_from_open_or_pending()
    {
        var fromOpen = CaseIn(CaseState.Open);
        fromOpen.StartInProgress(Actor, Now);
        fromOpen.State.ShouldBe(CaseState.InProgress);

        var fromPending = CaseIn(CaseState.Pending);
        fromPending.StartInProgress(Actor, Now);
        fromPending.State.ShouldBe(CaseState.InProgress);
    }

    [Fact]
    public void Reopening_returns_to_in_progress_and_clears_the_closing_timestamp()
    {
        var negotiationCase = ClosedCase();

        negotiationCase.Reopen(Actor, Now);

        negotiationCase.State.ShouldBe(CaseState.InProgress);
        negotiationCase.ClosedAt.ShouldBeNull();
        negotiationCase.PromotedAgreementId.ShouldBeNull();
    }

    [Fact]
    public void Reopening_is_only_allowed_from_closed()
    {
        var negotiationCase = CaseIn(CaseState.Agreed);

        Should.Throw<NegotiationRuleException>(() => negotiationCase.Reopen(Actor, Now))
            .CurrentState.ShouldBe(CaseState.Agreed);
    }

    [Fact]
    public void A_promoted_case_cannot_be_reopened_and_keeps_its_link()
    {
        var negotiationCase = ClosedCase();
        var agreementId = Guid.NewGuid();
        negotiationCase.Promote(agreementId, Actor, Now);

        var exception = Should.Throw<NegotiationRuleException>(() => negotiationCase.Reopen(Actor, Now));

        exception.Kind.ShouldBe(NegotiationRuleKind.InvalidState);
        exception.Message.ShouldContain("terminal");
        negotiationCase.State.ShouldBe(CaseState.Closed);
        negotiationCase.PromotedAgreementId.ShouldBe(agreementId);
    }

    [Fact]
    public void Promotion_is_only_allowed_from_closed_and_is_recorded_without_changing_the_state()
    {
        var negotiationCase = CaseIn(CaseState.Agreed);

        Should.Throw<NegotiationRuleException>(() => negotiationCase.Promote(Guid.NewGuid(), Actor, Now))
            .Kind.ShouldBe(NegotiationRuleKind.InvalidState);

        var closed = ClosedCase();
        var agreementId = Guid.NewGuid();
        closed.Promote(agreementId, Actor, Now);

        closed.State.ShouldBe(CaseState.Closed);
        closed.PromotedAgreementId.ShouldBe(agreementId);
    }

    [Fact]
    public void A_case_cannot_be_promoted_twice()
    {
        var negotiationCase = ClosedCase();
        negotiationCase.Promote(Guid.NewGuid(), Actor, Now);

        var exception = Should.Throw<NegotiationRuleException>(
            () => negotiationCase.Promote(Guid.NewGuid(), Actor, Now));

        exception.Kind.ShouldBe(NegotiationRuleKind.Conflict);
        exception.Code.ShouldBe("already_promoted");
    }

    [Fact]
    public void A_closed_case_refuses_child_writes_until_reopened()
    {
        var negotiationCase = ClosedCase();

        Should.Throw<NegotiationRuleException>(
                () => negotiationCase.AddPetition(2, "Otra", "Texto", null, Actor, Now))
            .Kind.ShouldBe(NegotiationRuleKind.InvalidState);

        Should.Throw<NegotiationRuleException>(
                () => negotiationCase.AddParty(PartyRole.Union, "Sindicato", null, null, null, Actor, Now))
            .Kind.ShouldBe(NegotiationRuleKind.InvalidState);

        negotiationCase.Reopen(Actor, Now);
        Should.NotThrow(() => negotiationCase.AddPetition(2, "Otra", "Texto", null, Actor, Now));
    }

    [Fact]
    public void An_agreed_petition_cannot_be_edited_or_deleted()
    {
        var negotiationCase = NewCase();
        var petition = negotiationCase.AddPetition(1, "Salarios", "Texto", null, Actor, Now);
        negotiationCase.ChangePetitionState(petition.Id, PetitionState.Agreed, Actor, Now);

        Should.Throw<NegotiationRuleException>(
                () => negotiationCase.UpdatePetition(petition.Id, 1, "Nuevo", "Texto", null, Actor, Now))
            .Kind.ShouldBe(NegotiationRuleKind.Conflict);

        Should.Throw<NegotiationRuleException>(() => negotiationCase.DeletePetition(petition.Id, Actor, Now))
            .Kind.ShouldBe(NegotiationRuleKind.Conflict);
    }

    [Fact]
    public void Petition_outcomes_follow_the_documented_minimum_graph()
    {
        var negotiationCase = NewCase();
        var petition = negotiationCase.AddPetition(1, "Salarios", "Texto", null, Actor, Now);

        // Open -> each outcome
        negotiationCase.ChangePetitionState(petition.Id, PetitionState.Rejected, Actor, Now);
        petition.State.ShouldBe(PetitionState.Rejected);

        // Rejected is terminal
        Should.Throw<NegotiationRuleException>(
                () => negotiationCase.ChangePetitionState(petition.Id, PetitionState.Open, Actor, Now))
            .Code.ShouldBe("illegal_petition_transition");

        var agreed = negotiationCase.AddPetition(2, "Vacaciones", "Texto", null, Actor, Now);
        negotiationCase.ChangePetitionState(agreed.Id, PetitionState.Agreed, Actor, Now);
        negotiationCase.ChangePetitionState(agreed.Id, PetitionState.Open, Actor, Now);
        agreed.State.ShouldBe(PetitionState.Open);

        var withdrawn = negotiationCase.AddPetition(3, "Bono", "Texto", null, Actor, Now);
        negotiationCase.ChangePetitionState(withdrawn.Id, PetitionState.Withdrawn, Actor, Now);
        withdrawn.State.ShouldBe(PetitionState.Withdrawn);
    }

    [Fact]
    public void Petition_numbers_are_unique_within_a_case()
    {
        var negotiationCase = NewCase();
        negotiationCase.AddPetition(1, "Uno", "Texto", null, Actor, Now);

        Should.Throw<NegotiationRuleException>(
                () => negotiationCase.AddPetition(1, "Otro", "Texto", null, Actor, Now))
            .Kind.ShouldBe(NegotiationRuleKind.Conflict);
    }

    [Fact]
    public void A_meeting_agreement_requires_a_meeting_of_the_same_case()
    {
        var negotiationCase = NewCase();
        var petition = negotiationCase.AddPetition(1, "Salarios", "Texto", null, Actor, Now);

        Should.Throw<NegotiationRuleException>(
                () => negotiationCase.AddMeetingAgreement(
                    petition.Id, Guid.NewGuid(), new DateOnly(2026, 1, 5), 1, "Salarios", "Acuerdo", null, Actor, Now))
            .Kind.ShouldBe(NegotiationRuleKind.Validation);
    }

    [Fact]
    public void A_meeting_with_agreements_cannot_be_deleted()
    {
        var negotiationCase = NewCase();
        var petition = negotiationCase.AddPetition(1, "Salarios", "Texto", null, Actor, Now);
        var meeting = negotiationCase.AddMeeting(new DateOnly(2026, 1, 5), null, null, null, null, null, Actor, Now);
        negotiationCase.AddMeetingAgreement(
            petition.Id, meeting.Id, new DateOnly(2026, 1, 5), 1, "Salarios", "Acuerdo", null, Actor, Now);

        Should.Throw<NegotiationRuleException>(() => negotiationCase.DeleteMeeting(meeting.Id, Actor, Now))
            .Kind.ShouldBe(NegotiationRuleKind.Conflict);
    }

    [Fact]
    public void A_party_email_must_be_an_address()
    {
        var negotiationCase = NewCase();

        Should.Throw<NegotiationRuleException>(
                () => negotiationCase.AddParty(PartyRole.Union, "Sindicato", null, "not-an-email", null, Actor, Now))
            .Kind.ShouldBe(NegotiationRuleKind.Validation);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static NegotiationCase NewCase() =>
        NegotiationCase.Create(new Guid("d3000000-0000-0000-0000-000000000001"), new DateOnly(2026, 1, 1), Actor, Now);

    /// <summary>A case put into the requested state by walking the legal chain.</summary>
    private static NegotiationCase CaseIn(CaseState state)
    {
        var negotiationCase = NewCase();

        if (state is CaseState.Open)
        {
            return negotiationCase;
        }

        negotiationCase.StartInProgress(Actor, Now);

        if (state is CaseState.InProgress)
        {
            return negotiationCase;
        }

        negotiationCase.MarkPending(Actor, Now);

        if (state is CaseState.Pending)
        {
            return negotiationCase;
        }

        negotiationCase.Agree(Actor, Now);
        return negotiationCase;
    }

    private static NegotiationCase ClosedCase()
    {
        var negotiationCase = CaseIn(CaseState.Agreed);
        var petition = negotiationCase.AddPetition(1, "Salarios", "Texto", Heading, Actor, Now);
        var meeting = negotiationCase.AddMeeting(new DateOnly(2026, 1, 5), null, null, null, null, null, Actor, Now);
        negotiationCase.AddMeetingAgreement(
            petition.Id, meeting.Id, new DateOnly(2026, 1, 5), 1, "Salarios", "Acuerdo", Heading, Actor, Now);
        negotiationCase.Close(Actor, Now);
        return negotiationCase;
    }
}
