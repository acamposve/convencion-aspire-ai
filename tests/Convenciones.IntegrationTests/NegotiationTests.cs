using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Convenciones.Api.Endpoints;
using Convenciones.Negotiation;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// The negotiation workflow: the documented chain, the illegal transitions, the read-only closed case,
/// and promotion as a separate, authorised, atomic and terminal operation.
/// </summary>
/// <remarks>
/// The database half is asserted alongside the API wherever a rule is enforced twice — the promotion
/// guard is exercised through Npgsql with the API bypassed, and the forced-failure and concurrency tests
/// assert row counts rather than HTTP statuses.
/// </remarks>
[Collection(NegotiationCollection.Name)]
public sealed class NegotiationTests(NegotiationFixture fixture) : IDisposable
{
    private readonly List<ApiFactory> _hosts = [];

    [Fact]
    public async Task The_full_lifecycle_records_every_transition_in_the_audit_trail()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var heading = await fixture.HeadingIdAsync("Salarios");

        var created = await CreateCaseAsync(client, new DateOnly(2026, 1, 1));
        created.State.ShouldBe("Open");

        await AddPartyAsync(client, created.Id, "Union", "Sindicato Único");
        var petition = await AddPetitionAsync(client, created.Id, 1, "Salarios", heading);
        var meeting = await AddMeetingAsync(client, created.Id, new DateOnly(2026, 1, 5));
        await AddMeetingAgreementAsync(client, created.Id, petition.Id, meeting.Id, 1);

        (await TransitionAsync(client, created.Id, "in-progress")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TransitionAsync(client, created.Id, "pending")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TransitionAsync(client, created.Id, "agree")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var closed = await TransitionAsync(client, created.Id, "close");

        closed.StatusCode.ShouldBe(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());
        var detail = await closed.Content.ReadFromJsonAsync<CaseDetail>();

        detail.ShouldNotBeNull();
        detail!.State.ShouldBe("Closed");
        detail.ClosedAt.ShouldNotBeNull();
        detail.PromotedAgreementId.ShouldBeNull();

        var row = await fixture.CaseRowAsync(created.Id);
        row.State.ShouldBe("Closed");
        row.ClosedAt.ShouldNotBeNull();

        var audit = await fixture.AuditRowsAsync("negotiation_case", created.Id);

        audit.ShouldAllBe(entry => entry.Module == "negotiation");

        var transitions = audit.Select(entry => (entry.FromState, entry.ToState)).ToArray();

        transitions.ShouldContain(("Open", "InProgress"));
        transitions.ShouldContain(("InProgress", "Pending"));
        transitions.ShouldContain(("Pending", "Agreed"));
        transitions.ShouldContain(("Agreed", "Closed"));
        audit.Last().ActorId.ShouldBe(NegotiationFixture.NegotiatorA);
        audit.Last().Action.ShouldBe("updated");

        var petitionAudit = await fixture.AuditRowsAsync("union_petition", petition.Id);
        petitionAudit.ShouldContain(entry => entry.Action == "created");
    }

    [Fact]
    public async Task An_illegal_transition_is_refused_with_409_and_the_current_state()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var created = await CreateCaseAsync(client, new DateOnly(2026, 2, 1));

        foreach (var step in new[] { "close", "agree", "pending", "reopen" })
        {
            var response = await TransitionAsync(client, created.Id, step);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, $"{step} from Open must be refused");
            var problem = await ReadProblemAsync(response);

            problem.Code.ShouldBe("invalid_state");
            problem.State.ShouldBe("Open");
        }

        (await fixture.CaseRowAsync(created.Id)).State.ShouldBe("Open");
    }

    [Fact]
    public async Task Closing_requires_at_least_one_meeting_agreement()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var created = await CreateCaseAsync(client, new DateOnly(2026, 3, 1));

        (await TransitionAsync(client, created.Id, "in-progress")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TransitionAsync(client, created.Id, "pending")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TransitionAsync(client, created.Id, "agree")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var close = await TransitionAsync(client, created.Id, "close");

        close.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(close);

        problem.Code.ShouldBe("invalid_state");
        problem.State.ShouldBe("Agreed");
        problem.Detail.ShouldNotBeNull();
        problem.Detail!.ShouldContain("meeting agreement");
        (await fixture.CaseRowAsync(created.Id)).State.ShouldBe("Agreed");
    }

    [Fact]
    public async Task Reopening_returns_to_in_progress_clears_the_timestamp_and_is_audited()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var closed = await WalkToClosedAsync(client, new DateOnly(2026, 4, 1));

        var reopen = await TransitionAsync(client, closed.CaseId, "reopen");

        reopen.StatusCode.ShouldBe(HttpStatusCode.OK, await reopen.Content.ReadAsStringAsync());
        var detail = await reopen.Content.ReadFromJsonAsync<CaseDetail>();

        detail!.State.ShouldBe("InProgress");
        detail.ClosedAt.ShouldBeNull();

        var row = await fixture.CaseRowAsync(closed.CaseId);
        row.State.ShouldBe("InProgress");
        row.ClosedAt.ShouldBeNull();

        var audit = await fixture.AuditRowsAsync("negotiation_case", closed.CaseId);
        audit.Last().FromState.ShouldBe("Closed");
        audit.Last().ToState.ShouldBe("InProgress");
        audit.Last().ActorId.ShouldBe(NegotiationFixture.NegotiatorA);
    }

    [Fact]
    public async Task Promotion_creates_the_agreement_and_the_link_together()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var closed = await WalkToClosedAsync(client, new DateOnly(2026, 5, 1));
        var before = await fixture.AgreementCountAsync(NegotiationFixture.CompanyA);

        var promote = await PromoteAsync(
            client, closed.CaseId, new DateOnly(2026, 6, 1), new DateOnly(2028, 6, 1), "Nacional");

        promote.StatusCode.ShouldBe(HttpStatusCode.OK, await promote.Content.ReadAsStringAsync());
        var result = await promote.Content.ReadFromJsonAsync<PromoteCaseResponse>();

        result.ShouldNotBeNull();
        result!.CaseId.ShouldBe(closed.CaseId);

        var agreement = await fixture.AgreementRowAsync(result.AgreementId);

        agreement.CompanyId.ShouldBe(NegotiationFixture.CompanyA);
        agreement.ValidFrom.ShouldBe(new DateOnly(2026, 6, 1));
        agreement.ValidTo.ShouldBe(new DateOnly(2028, 6, 1));
        agreement.Scope.ShouldBe("Nacional");
        agreement.State.ShouldBe("Draft");

        // Header only (D5): the meeting agreement is the negotiation record, not a clause.
        (await fixture.AgreementCountAsync(NegotiationFixture.CompanyA)).ShouldBe(before + 1);

        var row = await fixture.CaseRowAsync(closed.CaseId);
        row.State.ShouldBe("Closed");
        row.PromotedAgreementId.ShouldBe(result.AgreementId);
    }

    [Fact]
    public async Task A_promoted_case_is_terminal_and_cannot_be_promoted_again()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var closed = await WalkToClosedAsync(client, new DateOnly(2026, 7, 1));

        var first = await PromoteAsync(client, closed.CaseId, new DateOnly(2026, 8, 1), null, "Nacional");
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var result = await first.Content.ReadFromJsonAsync<PromoteCaseResponse>();
        var agreementsAfterFirst = await fixture.AgreementCountAsync(NegotiationFixture.CompanyA);

        var second = await PromoteAsync(client, closed.CaseId, new DateOnly(2026, 9, 1), null, "Nacional");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(second)).Code.ShouldBe("conflict");

        var reopen = await TransitionAsync(client, closed.CaseId, "reopen");
        reopen.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(reopen);
        problem.Code.ShouldBe("invalid_state");
        problem.State.ShouldBe("Closed");

        var row = await fixture.CaseRowAsync(closed.CaseId);
        row.State.ShouldBe("Closed");
        row.PromotedAgreementId.ShouldBe(result!.AgreementId);
        (await fixture.AgreementCountAsync(NegotiationFixture.CompanyA)).ShouldBe(agreementsAfterFirst);
    }

    [Fact]
    public async Task Promotion_requires_the_promote_capability()
    {
        using var writer = ClientFor(NegotiationFixture.WriterWithoutPromote);
        var closed = await WalkToClosedAsync(writer, new DateOnly(2026, 10, 1));
        var before = await fixture.AgreementCountAsync(NegotiationFixture.CompanyA);

        var promote = await PromoteAsync(writer, closed.CaseId, new DateOnly(2026, 11, 1), null, "Nacional");

        promote.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await ReadProblemAsync(promote);
        problem.Code.ShouldBe("forbidden");
        problem.Detail.ShouldNotBeNull();
        problem.Detail!.ShouldContain("negotiation.promote");

        (await fixture.AgreementCountAsync(NegotiationFixture.CompanyA)).ShouldBe(before);
        (await fixture.CaseRowAsync(closed.CaseId)).PromotedAgreementId.ShouldBeNull();
    }

    [Fact]
    public async Task The_database_refuses_promotion_without_the_capability_when_the_api_is_bypassed()
    {
        var caseId = Guid.NewGuid();
        var agreementId = Guid.NewGuid();
        await fixture.InsertCaseAsync(caseId, NegotiationFixture.CompanyA, "Closed", closed: true);
        await fixture.InsertAgreementAsync(agreementId, NegotiationFixture.CompanyA, new DateOnly(2026, 12, 1));

        var exception = await fixture.TryAsUserAsync(
            NegotiationFixture.WriterWithoutPromote,
            $"update app.negotiation_case set promoted_agreement_id = '{agreementId}' where id = '{caseId}'");

        exception.ShouldNotBeNull("the guard must refuse a promotion without negotiation.promote");
        exception!.SqlState.ShouldBe("42501");
        exception.MessageText.ShouldContain("negotiation.promote");
        (await fixture.CaseRowAsync(caseId)).PromotedAgreementId.ShouldBeNull();
    }

    [Fact]
    public async Task The_database_refuses_promotion_from_a_case_that_was_not_closed()
    {
        var openCaseId = Guid.NewGuid();
        var closedInOneStatementId = Guid.NewGuid();
        var agreementId = Guid.NewGuid();

        await fixture.InsertCaseAsync(openCaseId, NegotiationFixture.CompanyA, "Open", closed: false);
        await fixture.InsertCaseAsync(closedInOneStatementId, NegotiationFixture.CompanyA, "Open", closed: false);
        await fixture.InsertAgreementAsync(agreementId, NegotiationFixture.CompanyA, new DateOnly(2027, 1, 1));

        // Set the link while the case stays Open: the guard refuses before the CHECK can.
        var openRefusal = await fixture.TryAsUserAsync(
            NegotiationFixture.NegotiatorA,
            $"update app.negotiation_case set promoted_agreement_id = '{agreementId}' where id = '{openCaseId}'");

        openRefusal.ShouldNotBeNull();
        openRefusal!.SqlState.ShouldBe("22023");
        openRefusal.MessageText.ShouldContain("only be promoted from Closed");

        // Close and promote in one statement: the previous schema allowed this, the guard does not.
        var togetherRefusal = await fixture.TryAsUserAsync(
            NegotiationFixture.NegotiatorA,
            $"""
             update app.negotiation_case
             set state = 'Closed', closed_at = now(), promoted_agreement_id = '{agreementId}'
             where id = '{closedInOneStatementId}'
             """);

        togetherRefusal.ShouldNotBeNull("a case cannot be closed and promoted in one statement");
        togetherRefusal!.SqlState.ShouldBe("22023");
        (await fixture.CaseRowAsync(closedInOneStatementId)).State.ShouldBe("Open");
    }

    [Fact]
    public async Task The_database_refuses_repointing_or_clearing_a_promoted_case()
    {
        var caseId = Guid.NewGuid();
        var firstAgreement = Guid.NewGuid();
        var secondAgreement = Guid.NewGuid();

        await fixture.InsertCaseAsync(caseId, NegotiationFixture.CompanyA, "Closed", closed: true);
        await fixture.InsertAgreementAsync(firstAgreement, NegotiationFixture.CompanyA, new DateOnly(2027, 2, 1));
        await fixture.InsertAgreementAsync(secondAgreement, NegotiationFixture.CompanyA, new DateOnly(2027, 3, 1));

        // A legitimate promotion, committed so the terminal rules can be exercised afterwards.
        await fixture.ExecuteAsUserAsync(
            NegotiationFixture.NegotiatorA,
            $"update app.negotiation_case set promoted_agreement_id = '{firstAgreement}' where id = '{caseId}'");

        (await fixture.CaseRowAsync(caseId)).PromotedAgreementId.ShouldBe(firstAgreement);

        var repoint = await fixture.TryAsUserAsync(
            NegotiationFixture.NegotiatorA,
            $"update app.negotiation_case set promoted_agreement_id = '{secondAgreement}' where id = '{caseId}'");

        repoint.ShouldNotBeNull("a case cannot be re-pointed at a second agreement");
        repoint!.SqlState.ShouldBe("23505");

        var clear = await fixture.TryAsUserAsync(
            NegotiationFixture.NegotiatorA,
            $"update app.negotiation_case set promoted_agreement_id = null where id = '{caseId}'");

        clear.ShouldNotBeNull("the promotion link cannot be cleared");
        clear!.SqlState.ShouldBe("22023");
        clear.MessageText.ShouldContain("terminal");
    }

    [Fact]
    public async Task A_forced_failure_after_the_agreement_is_created_leaves_neither()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var closed = await WalkToClosedAsync(client, new DateOnly(2027, 4, 1));
        var before = await fixture.AgreementCountAsync(NegotiationFixture.CompanyA);

        await fixture.InstallForcedFailureTriggerAsync();

        try
        {
            var promote = await PromoteAsync(client, closed.CaseId, new DateOnly(2027, 5, 1), null, "Nacional");

            promote.StatusCode.ShouldBe(HttpStatusCode.Conflict);

            // The assertion is the database state, not the status: the agreement INSERT must have been
            // rolled back with the case UPDATE it was part of.
            (await fixture.AgreementCountAsync(NegotiationFixture.CompanyA)).ShouldBe(before);
            (await fixture.CaseRowAsync(closed.CaseId)).PromotedAgreementId.ShouldBeNull();
        }
        finally
        {
            await fixture.RemoveForcedFailureTriggerAsync();
        }
    }

    [Fact]
    public async Task Concurrent_promotions_produce_exactly_one_agreement()
    {
        using var firstClient = ClientFor(NegotiationFixture.NegotiatorA);
        var closed = await WalkToClosedAsync(firstClient, new DateOnly(2027, 6, 1));
        var before = await fixture.AgreementCountAsync(NegotiationFixture.CompanyA);

        using var secondClient = ClientFor(NegotiationFixture.NegotiatorA);

        // Different start dates on purpose: the agreement identity index must not be what decides the
        // race here — the case guard is what must refuse the second promotion.
        var first = PromoteAsync(firstClient, closed.CaseId, new DateOnly(2027, 7, 1), null, "Nacional");
        var second = PromoteAsync(secondClient, closed.CaseId, new DateOnly(2027, 8, 1), null, "Nacional");

        var responses = await Task.WhenAll(first, second);

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        (await fixture.AgreementCountAsync(NegotiationFixture.CompanyA)).ShouldBe(before + 1);

        var row = await fixture.CaseRowAsync(closed.CaseId);
        row.PromotedAgreementId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Another_company_cannot_see_or_change_the_case()
    {
        using var editorA = ClientFor(NegotiationFixture.NegotiatorA);
        var closed = await WalkToClosedAsync(editorA, new DateOnly(2027, 9, 1));

        using var negotiatorB = ClientFor(NegotiationFixture.NegotiatorB);

        (await negotiatorB.GetAsync(new Uri($"/negotiation/cases/{closed.CaseId}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await TransitionAsync(negotiatorB, closed.CaseId, "reopen"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await PromoteAsync(negotiatorB, closed.CaseId, new DateOnly(2027, 10, 1), null, "Nacional"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var visible = await fixture.ScalarAsUserAsync(
            NegotiationFixture.NegotiatorB,
            $"select count(*) from app.negotiation_case where id = '{closed.CaseId}'");

        visible.ShouldBe(0);
    }

    [Fact]
    public async Task A_closed_case_refuses_child_writes_until_reopened()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var closed = await WalkToClosedAsync(client, new DateOnly(2027, 11, 1));

        var petition = await client.PostAsJsonAsync(
            new Uri($"/negotiation/cases/{closed.CaseId}/petitions", UriKind.Relative),
            new PetitionRequest(9, "Otra", "Texto", null));

        petition.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(petition);

        problem.Code.ShouldBe("invalid_state");
        problem.State.ShouldBe("Closed");

        var party = await client.PostAsJsonAsync(
            new Uri($"/negotiation/cases/{closed.CaseId}/parties", UriKind.Relative),
            new PartyRequest("Union", "Sindicato", null, null, null));

        party.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await TransitionAsync(client, closed.CaseId, "reopen")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var afterReopen = await client.PostAsJsonAsync(
            new Uri($"/negotiation/cases/{closed.CaseId}/petitions", UriKind.Relative),
            new PetitionRequest(9, "Otra", "Texto", null));

        afterReopen.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Petition_outcomes_follow_the_documented_graph_and_an_agreed_petition_is_frozen()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var created = await CreateCaseAsync(client, new DateOnly(2027, 12, 1));
        var petition = await AddPetitionAsync(client, created.Id, 1, "Salarios", null);

        var agreed = await ChangePetitionStateAsync(client, created.Id, petition.Id, "Agreed");
        agreed.StatusCode.ShouldBe(HttpStatusCode.OK, await agreed.Content.ReadAsStringAsync());
        (await agreed.Content.ReadFromJsonAsync<PetitionResponse>())!.State.ShouldBe("Agreed");

        var edit = await client.PatchAsJsonAsync(
            new Uri($"/negotiation/cases/{created.Id}/petitions/{petition.Id}", UriKind.Relative),
            new PetitionRequest(1, "Cambiado", "Texto", null));

        edit.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await ChangePetitionStateAsync(client, created.Id, petition.Id, "Open"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var reopened = await client.PatchAsJsonAsync(
            new Uri($"/negotiation/cases/{created.Id}/petitions/{petition.Id}", UriKind.Relative),
            new PetitionRequest(1, "Cambiado", "Texto", null));

        reopened.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ChangePetitionStateAsync(client, created.Id, petition.Id, "Rejected"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var illegal = await ChangePetitionStateAsync(client, created.Id, petition.Id, "Open");
        illegal.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(illegal)).Code.ShouldBe("conflict");

        var audit = await fixture.AuditRowsAsync("union_petition", petition.Id);
        audit.Select(entry => (entry.FromState, entry.ToState)).ShouldContain(("Open", "Agreed"));
    }

    [Fact]
    public async Task The_promotion_payload_is_validated()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var closed = await WalkToClosedAsync(client, new DateOnly(2028, 1, 1));
        var before = await fixture.AgreementCountAsync(NegotiationFixture.CompanyA);

        var placeholder = await PromoteAsync(
            client, closed.CaseId, new DateOnly(2028, 2, 1), null, "*Ambito");

        placeholder.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(placeholder)).Code.ShouldBe("validation_failed");

        var blankScope = await PromoteAsync(client, closed.CaseId, new DateOnly(2028, 2, 1), null, "   ");
        blankScope.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var reversedDates = await PromoteAsync(
            client, closed.CaseId, new DateOnly(2028, 2, 1), new DateOnly(2027, 1, 1), "Nacional");

        reversedDates.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.AgreementCountAsync(NegotiationFixture.CompanyA)).ShouldBe(before);
        (await fixture.CaseRowAsync(closed.CaseId)).PromotedAgreementId.ShouldBeNull();
    }

    [Fact]
    public async Task The_list_is_company_scoped_and_filters_by_state()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var created = await CreateCaseAsync(client, new DateOnly(2028, 3, 1));

        var list = await client.GetFromJsonAsync<CaseListResponse>(
            new Uri("/negotiation/cases?state=Open&page=1&pageSize=100", UriKind.Relative));

        list.ShouldNotBeNull();
        list!.Items.ShouldContain(item => item.Id == created.Id);
        list.Items.ShouldAllBe(item => item.State == "Open");
        list.Items.ShouldAllBe(item => item.CompanyId == NegotiationFixture.CompanyA);

        var invalidState = await client.GetAsync(new Uri("/negotiation/cases?state=Draft", UriKind.Relative));
        invalidState.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(invalidState)).Code.ShouldBe("validation_failed");
    }

    [Fact]
    public async Task An_unknown_case_is_not_found()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var missing = Guid.NewGuid();

        (await client.GetAsync(new Uri($"/negotiation/cases/{missing}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await TransitionAsync(client, missing, "close")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await PromoteAsync(client, missing, new DateOnly(2028, 4, 1), null, "Nacional"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Parties_offers_and_meeting_agreements_can_be_added_and_removed()
    {
        using var client = ClientFor(NegotiationFixture.NegotiatorA);
        var created = await CreateCaseAsync(client, new DateOnly(2028, 5, 1));
        var party = await AddPartyAsync(client, created.Id, "Union", "Sindicato");
        var petition = await AddPetitionAsync(client, created.Id, 1, "Salarios", null);
        var meeting = await AddMeetingAsync(client, created.Id, new DateOnly(2028, 5, 3));

        var offer = await client.PostAsJsonAsync(
            new Uri($"/negotiation/cases/{created.Id}/petitions/{petition.Id}/offers", UriKind.Relative),
            new OfferRequest(1, "Contraoferta", "Cuerpo"));

        offer.StatusCode.ShouldBe(HttpStatusCode.Created, await offer.Content.ReadAsStringAsync());
        var offerBody = await offer.Content.ReadFromJsonAsync<OfferResponse>();

        var agreement = await AddMeetingAgreementAsync(client, created.Id, petition.Id, meeting.Id, 1);

        var detail = await client.GetFromJsonAsync<CaseDetail>(
            new Uri($"/negotiation/cases/{created.Id}", UriKind.Relative));

        detail.ShouldNotBeNull();
        detail!.Parties.ShouldContain(item => item.Id == party.Id);
        detail.Petitions.Single(item => item.Id == petition.Id).Offers
            .ShouldContain(item => item.Id == offerBody!.Id);
        detail.Petitions.Single(item => item.Id == petition.Id).Agreements
            .ShouldContain(item => item.Id == agreement.Id);

        (await client.DeleteAsync(new Uri(
                $"/negotiation/cases/{created.Id}/petitions/{petition.Id}/agreements/{agreement.Id}",
                UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.DeleteAsync(new Uri(
                $"/negotiation/cases/{created.Id}/petitions/{petition.Id}/offers/{offerBody!.Id}",
                UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.DeleteAsync(new Uri(
                $"/negotiation/cases/{created.Id}/parties/{party.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.DeleteAsync(new Uri(
                $"/negotiation/cases/{created.Id}/meetings/{meeting.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await client.GetFromJsonAsync<CaseDetail>(
            new Uri($"/negotiation/cases/{created.Id}", UriKind.Relative));

        after.ShouldNotBeNull();
        after!.Parties.ShouldBeEmpty();
        after.Meetings.ShouldBeEmpty();
        after.Petitions.Single(item => item.Id == petition.Id).Offers.ShouldBeEmpty();
        after.Petitions.Single(item => item.Id == petition.Id).Agreements.ShouldBeEmpty();
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private HttpClient ClientFor(Guid subject)
    {
        var factory = new ApiFactory(fixture.ConnectionString);
        _hosts.Add(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(subject));
        return client;
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }

    private static async Task<CaseDetail> CreateCaseAsync(HttpClient client, DateOnly startedOn)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/negotiation/cases", UriKind.Relative),
            new CreateCaseRequest(null, startedOn));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var detail = await response.Content.ReadFromJsonAsync<CaseDetail>();
        detail.ShouldNotBeNull();
        return detail!;
    }

    private static async Task<PartyResponse> AddPartyAsync(
        HttpClient client,
        Guid caseId,
        string role,
        string name)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/negotiation/cases/{caseId}/parties", UriKind.Relative),
            new PartyRequest(role, name, null, null, "Representante"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var party = await response.Content.ReadFromJsonAsync<PartyResponse>();
        party.ShouldNotBeNull();
        return party!;
    }

    private static async Task<PetitionResponse> AddPetitionAsync(
        HttpClient client,
        Guid caseId,
        int number,
        string title,
        Guid? headingId)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/negotiation/cases/{caseId}/petitions", UriKind.Relative),
            new PetitionRequest(number, title, "Texto de la petición.", headingId));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var petition = await response.Content.ReadFromJsonAsync<PetitionResponse>();
        petition.ShouldNotBeNull();
        return petition!;
    }

    private static async Task<MeetingResponse> AddMeetingAsync(
        HttpClient client,
        Guid caseId,
        DateOnly heldOn)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/negotiation/cases/{caseId}/meetings", UriKind.Relative),
            new MeetingRequest(heldOn, new TimeOnly(9, 0), "2h", "Reunión", null, "Partes"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var meeting = await response.Content.ReadFromJsonAsync<MeetingResponse>();
        meeting.ShouldNotBeNull();
        return meeting!;
    }

    private static async Task<MeetingAgreementResponse> AddMeetingAgreementAsync(
        HttpClient client,
        Guid caseId,
        Guid petitionId,
        Guid meetingId,
        int number)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/negotiation/cases/{caseId}/petitions/{petitionId}/agreements", UriKind.Relative),
            new MeetingAgreementRequest(new DateOnly(2026, 1, 5), meetingId, number, "Salarios", "Cuerpo", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var agreement = await response.Content.ReadFromJsonAsync<MeetingAgreementResponse>();
        agreement.ShouldNotBeNull();
        return agreement!;
    }

    private static Task<HttpResponseMessage> TransitionAsync(HttpClient client, Guid caseId, string step) =>
        client.PostAsync(new Uri($"/negotiation/cases/{caseId}/{step}", UriKind.Relative), content: null);

    private static Task<HttpResponseMessage> ChangePetitionStateAsync(
        HttpClient client,
        Guid caseId,
        Guid petitionId,
        string state) =>
        client.PutAsJsonAsync(
            new Uri($"/negotiation/cases/{caseId}/petitions/{petitionId}/state", UriKind.Relative),
            new PetitionStateRequest(state));

    private static Task<HttpResponseMessage> PromoteAsync(
        HttpClient client,
        Guid caseId,
        DateOnly validFrom,
        DateOnly? validTo,
        string? scope) =>
        client.PostAsJsonAsync(
            new Uri($"/negotiation/cases/{caseId}/promote", UriKind.Relative),
            new PromoteCaseRequest(validFrom, validTo, scope));

    private async Task<ClosedCase> WalkToClosedAsync(HttpClient client, DateOnly startedOn)
    {
        var heading = await fixture.HeadingIdAsync("Salarios");
        var created = await CreateCaseAsync(client, startedOn);
        var petition = await AddPetitionAsync(client, created.Id, 1, "Salarios", heading);
        var meeting = await AddMeetingAsync(client, created.Id, startedOn.AddDays(2));
        var agreement = await AddMeetingAgreementAsync(client, created.Id, petition.Id, meeting.Id, 1);

        (await TransitionAsync(client, created.Id, "in-progress")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TransitionAsync(client, created.Id, "pending")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TransitionAsync(client, created.Id, "agree")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var close = await TransitionAsync(client, created.Id, "close");
        close.StatusCode.ShouldBe(HttpStatusCode.OK, await close.Content.ReadAsStringAsync());

        return new ClosedCase(created.Id, petition.Id, meeting.Id, agreement.Id);
    }

    private static async Task<(string? Code, string? Detail, string? State)> ReadProblemAsync(
        HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        return (
            root.TryGetProperty("code", out var code) ? code.GetString() : null,
            root.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
            root.TryGetProperty("state", out var state) ? state.GetString() : null);
    }

    private sealed record ClosedCase(Guid CaseId, Guid PetitionId, Guid MeetingId, Guid MeetingAgreementId);
}
