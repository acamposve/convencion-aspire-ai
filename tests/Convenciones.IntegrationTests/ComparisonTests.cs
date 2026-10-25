using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Convenciones.Api.Endpoints;
using Convenciones.Comparison;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// The comparison read surface: excluded headings never pollute it, drafts are never rendered,
/// clauses are grouped by heading in numeric order without claiming alignment, and the result is
/// paged and company-scoped.
/// </summary>
[Collection(AgreementsCollection.Name)]
public sealed class ComparisonTests(AgreementFixture fixture) : IDisposable
{
    private readonly List<ApiFactory> _hosts = [];

    [Fact]
    public async Task An_excluded_heading_never_yields_comparison_rows()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var excludedHeading = await fixture.HeadingIdAsync("No Compara");

        var agreement = await CreateAgreementAsync(client, new DateOnly(2027, 1, 1));
        await AddClauseAsync(
            client,
            agreement.Id,
            1,
            excludedHeading,
            "Cláusula de la exclusión",
            "Este texto no debe aparecer en una comparación.");
        await PublishAsync(client, agreement.Id);

        var candidates = await client.GetFromJsonAsync<ComparisonCandidatePage>(
            new Uri(
                $"/comparison/candidates?headingId={excludedHeading}&page=1&pageSize=50",
                UriKind.Relative));

        candidates.ShouldNotBeNull();
        candidates!.Total.ShouldBe(0);
        candidates.Items.ShouldBeEmpty();

        var response = await client.GetAsync(
            new Uri(
                $"/comparison?headingId={excludedHeading}&agreementIds={agreement.Id}",
                UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var comparison = await response.Content.ReadFromJsonAsync<ComparisonView>();
        comparison.ShouldNotBeNull();
        comparison!.Heading.IsExcluded.ShouldBeTrue();
        comparison.Agreements.ShouldBeEmpty();
        comparison.Total.ShouldBe(0);

        var json = await response.Content.ReadAsStringAsync();
        json.ShouldNotContain("Cláusula de la exclusión");
        json.ShouldNotContain("Este texto no debe aparecer en una comparación.");
    }

    [Fact]
    public async Task An_unpublished_agreement_is_never_rendered_but_is_reported()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var heading = await fixture.HeadingIdAsync("Salarios");

        var agreement = await CreateAgreementAsync(client, new DateOnly(2027, 2, 1));
        await AddClauseAsync(
            client,
            agreement.Id,
            1,
            heading,
            "Cláusula en borrador",
            "Cuerpo del borrador.");

        var beforePublish = await client.GetFromJsonAsync<ComparisonCandidatePage>(
            new Uri($"/comparison/candidates?headingId={heading}&page=1&pageSize=50", UriKind.Relative));
        beforePublish.ShouldNotBeNull();
        beforePublish!.Items.Select(item => item.AgreementId).ShouldNotContain(agreement.Id);

        var draftResponse = await client.GetAsync(
            new Uri(
                $"/comparison?headingId={heading}&agreementIds={agreement.Id}",
                UriKind.Relative));
        draftResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var draftComparison = await draftResponse.Content.ReadFromJsonAsync<ComparisonView>();
        draftComparison.ShouldNotBeNull();
        draftComparison!.Agreements.ShouldBeEmpty();
        draftComparison.UnpublishedRequested.ShouldContain(agreement.Id);
        draftComparison.Total.ShouldBe(0);
        (await draftResponse.Content.ReadAsStringAsync()).ShouldNotContain("Cláusula en borrador");

        await PublishAsync(client, agreement.Id);

        var publishedResponse = await client.GetAsync(
            new Uri(
                $"/comparison?headingId={heading}&agreementIds={agreement.Id}",
                UriKind.Relative));
        publishedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var published = await publishedResponse.Content.ReadFromJsonAsync<ComparisonView>();
        published.ShouldNotBeNull();
        published!.Agreements.Count.ShouldBe(1);
        published.Agreements[0].AgreementId.ShouldBe(agreement.Id);
        published.Agreements[0].Clauses.ShouldContain(clause => clause.Title == "Cláusula en borrador");
        published.UnpublishedRequested.ShouldBeEmpty();
    }

    [Fact]
    public async Task Candidates_are_published_company_scoped_and_paged()
    {
        using var editorA = ClientFor(AgreementFixture.EditorA);
        using var editorB = ClientFor(AgreementFixture.EditorB);
        var heading = await fixture.HeadingIdAsync("Salarios");

        var first = await CreatePublishedAsync(editorA, new DateOnly(2027, 3, 1), heading);
        var second = await CreatePublishedAsync(editorA, new DateOnly(2027, 3, 2), heading);
        var third = await CreatePublishedAsync(editorA, new DateOnly(2027, 3, 3), heading);
        var companyB = await CreatePublishedAsync(editorB, new DateOnly(2027, 3, 4), heading);

        var expectedTotal = await fixture.PublishedCandidateCountAsync(
            AgreementFixture.CompanyA, heading);

        var firstPage = await editorA.GetFromJsonAsync<ComparisonCandidatePage>(
            new Uri(
                $"/comparison/candidates?headingId={heading}&companyId={AgreementFixture.CompanyA}&page=1&pageSize=2",
                UriKind.Relative));
        firstPage.ShouldNotBeNull();
        firstPage!.Total.ShouldBe(expectedTotal);
        firstPage.Items.Count.ShouldBe(2);
        firstPage.Items.ShouldAllBe(candidate => candidate.CompanyId == AgreementFixture.CompanyA);
        firstPage.Items[0].ValidFrom.ShouldBeGreaterThanOrEqualTo(firstPage.Items[1].ValidFrom);
        firstPage.Items[0].Title.ShouldContain("Agreements Company A");
        firstPage.Items[0].ClauseCount.ShouldBeGreaterThan(0);

        var secondPage = await editorA.GetFromJsonAsync<ComparisonCandidatePage>(
            new Uri(
                $"/comparison/candidates?headingId={heading}&companyId={AgreementFixture.CompanyA}&page=2&pageSize=2",
                UriKind.Relative));
        secondPage.ShouldNotBeNull();
        secondPage!.Total.ShouldBe(expectedTotal);
        firstPage.Items.Select(item => item.AgreementId)
            .Intersect(secondPage.Items.Select(item => item.AgreementId))
            .ShouldBeEmpty();

        var all = await editorA.GetFromJsonAsync<ComparisonCandidatePage>(
            new Uri(
                $"/comparison/candidates?headingId={heading}&companyId={AgreementFixture.CompanyA}&page=1&pageSize=50",
                UriKind.Relative));
        all.ShouldNotBeNull();
        var allIds = all!.Items.Select(item => item.AgreementId).ToArray();
        allIds.ShouldContain(first.Agreement.Id);
        allIds.ShouldContain(second.Agreement.Id);
        allIds.ShouldContain(third.Agreement.Id);
        allIds.ShouldNotContain(companyB.Agreement.Id);

        var companyBCandidates = await editorB.GetFromJsonAsync<ComparisonCandidatePage>(
            new Uri(
                $"/comparison/candidates?headingId={heading}&companyId={AgreementFixture.CompanyB}&page=1&pageSize=50",
                UriKind.Relative));
        companyBCandidates.ShouldNotBeNull();
        companyBCandidates!.Items.Select(item => item.AgreementId).ShouldContain(companyB.Agreement.Id);

        // Company A may not request company B's candidate page, even though the heading is global.
        var forbidden = await editorA.GetAsync(
            new Uri(
                $"/comparison/candidates?headingId={heading}&companyId={AgreementFixture.CompanyB}",
                UriKind.Relative));
        forbidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(forbidden)).ShouldBe("not_found");
    }

    [Fact]
    public async Task Comparison_groups_clauses_by_heading_in_stable_order_and_returns_the_economic_note()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var heading = await fixture.HeadingIdAsync("Salarios");
        var otherHeading = await fixture.HeadingIdAsync("Vacaciones");

        var older = await CreateAgreementAsync(client, new DateOnly(2027, 4, 1));
        await AddClauseAsync(client, older.Id, 10, heading, "Salario 10", "Cuerpo 10", "Bono mensual");
        await AddClauseAsync(client, older.Id, 2, heading, "Salario 2", "Cuerpo 2");
        await AddClauseAsync(client, older.Id, 1, otherHeading, "Vacaciones ajenas", "No pertenece");
        await PublishAsync(client, older.Id);

        var newer = await CreateAgreementAsync(client, new DateOnly(2027, 4, 2));
        await AddClauseAsync(client, newer.Id, 10, heading, "Salario nuevo 10", "Cuerpo nuevo 10");
        await AddClauseAsync(client, newer.Id, 2, heading, "Salario nuevo 2", "Cuerpo nuevo 2");
        await PublishAsync(client, newer.Id);

        var response = await client.GetAsync(
            new Uri(
                $"/comparison?headingId={heading}&agreementIds={older.Id},{newer.Id}&page=1&pageSize=5",
                UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var comparison = await response.Content.ReadFromJsonAsync<ComparisonView>();
        comparison.ShouldNotBeNull();
        comparison!.Heading.Id.ShouldBe(heading);
        comparison.Heading.GroupName.ShouldBe("GENERALES");
        comparison.Heading.IsExcluded.ShouldBeFalse();
        comparison.StatuteArticles.ShouldBeEmpty();
        comparison.Total.ShouldBe(2);
        comparison.Agreements.Count.ShouldBe(2);

        // Newest agreement first; this is the documented stable order.
        comparison.Agreements[0].AgreementId.ShouldBe(newer.Id);
        comparison.Agreements[1].AgreementId.ShouldBe(older.Id);

        var olderView = comparison.Agreements[1];
        olderView.Clauses.Select(clause => clause.Number).ShouldBe(new[] { 2, 10 });
        olderView.Clauses.Select(clause => clause.Title).ShouldNotContain("Vacaciones ajenas");
        olderView.Clauses.Single(clause => clause.Number == 10).EconomicComparisonNote.ShouldBe(
            "Bono mensual");

        var firstPage = await client.GetFromJsonAsync<ComparisonView>(
            new Uri(
                $"/comparison?headingId={heading}&agreementIds={older.Id},{newer.Id}&page=1&pageSize=1",
                UriKind.Relative));
        firstPage.ShouldNotBeNull();
        firstPage!.Total.ShouldBe(2);
        firstPage.Agreements.Count.ShouldBe(1);
        firstPage.Agreements[0].AgreementId.ShouldBe(newer.Id);

        var secondPage = await client.GetFromJsonAsync<ComparisonView>(
            new Uri(
                $"/comparison?headingId={heading}&agreementIds={older.Id},{newer.Id}&page=2&pageSize=1",
                UriKind.Relative));
        secondPage.ShouldNotBeNull();
        secondPage!.Agreements.Single().AgreementId.ShouldBe(older.Id);
    }

    [Fact]
    public async Task Another_companys_agreement_is_404_and_mixed_requests_return_nothing()
    {
        using var editorA = ClientFor(AgreementFixture.EditorA);
        using var editorB = ClientFor(AgreementFixture.EditorB);
        var heading = await fixture.HeadingIdAsync("Salarios");

        var own = await CreatePublishedAsync(editorA, new DateOnly(2027, 5, 1), heading);
        var foreign = await CreatePublishedAsync(editorB, new DateOnly(2027, 5, 2), heading);

        var foreignOnly = await editorA.GetAsync(
            new Uri(
                $"/comparison?headingId={heading}&agreementIds={foreign.Agreement.Id}",
                UriKind.Relative));
        foreignOnly.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(foreignOnly)).ShouldBe("not_found");

        var mixed = await editorA.GetAsync(
            new Uri(
                $"/comparison?headingId={heading}&agreementIds={own.Agreement.Id},{foreign.Agreement.Id}",
                UriKind.Relative));
        mixed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await mixed.Content.ReadAsStringAsync()).ShouldNotContain(own.Clause.Title);

        var missing = await editorA.GetAsync(
            new Uri(
                $"/comparison?headingId={heading}&agreementIds={Guid.NewGuid()}",
                UriKind.Relative));
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var asB = await editorB.GetAsync(
            new Uri(
                $"/comparison?headingId={heading}&agreementIds={foreign.Agreement.Id}",
                UriKind.Relative));
        asB.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Comparison_is_read_only_bounded_and_rejects_invalid_requests()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var heading = await fixture.HeadingIdAsync("Salarios");
        var published = await CreatePublishedAsync(client, new DateOnly(2027, 6, 1), heading);

        (await client.PostAsync(
                new Uri($"/comparison?headingId={heading}&agreementIds={published.Agreement.Id}", UriKind.Relative),
                content: null))
            .StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        (await client.PutAsync(
                new Uri($"/comparison?headingId={heading}&agreementIds={published.Agreement.Id}", UriKind.Relative),
                content: null))
            .StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        var tooMany = string.Join(",", Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()));
        var tooManyResponse = await client.GetAsync(
            new Uri($"/comparison?headingId={heading}&agreementIds={tooMany}", UriKind.Relative));
        tooManyResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(tooManyResponse)).ShouldBe("validation_failed");

        var missingIds = await client.GetAsync(
            new Uri($"/comparison?headingId={heading}", UriKind.Relative));
        missingIds.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var invalidId = await client.GetAsync(
            new Uri($"/comparison?headingId={heading}&agreementIds=not-a-guid", UriKind.Relative));
        invalidId.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var clamped = await client.GetFromJsonAsync<ComparisonView>(
            new Uri(
                $"/comparison?headingId={heading}&agreementIds={published.Agreement.Id}&page=1&pageSize=100",
                UriKind.Relative));
        clamped.ShouldNotBeNull();
        clamped!.PageSize.ShouldBe(5, "the comparison page size is bounded");

        var unknownHeading = await client.GetAsync(
            new Uri($"/comparison/candidates?headingId={Guid.NewGuid()}", UriKind.Relative));
        unknownHeading.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Heading_groups_are_ordered_and_flag_the_economic_comparison_requirement()
    {
        using var client = ClientFor(AgreementFixture.EditorA);

        var groups = await client.GetFromJsonAsync<HeadingGroupView[]>(
            new Uri("/comparison/heading-groups", UriKind.Relative));

        groups.ShouldNotBeNull();
        groups!.ShouldNotBeEmpty();
        groups.Select(group => group.SortOrder).ShouldBe(
            groups.Select(group => group.SortOrder).OrderBy(order => order));

        var generales = groups.Single(group => group.Name == "GENERALES");
        var economico = groups.Single(group => group.Name == "ECONOMICO");
        generales.RequiresEconomicComparison.ShouldBeFalse();
        economico.RequiresEconomicComparison.ShouldBeTrue();
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

    private static async Task<AgreementDetail> CreateAgreementAsync(
        HttpClient client,
        DateOnly validFrom,
        string scope = "Nacional")
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/agreements", UriKind.Relative),
            new CreateAgreementRequest(null, validFrom, null, scope));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var agreement = await response.Content.ReadFromJsonAsync<AgreementDetail>();
        agreement.ShouldNotBeNull();
        return agreement!;
    }

    private static async Task<ClauseResponse> AddClauseAsync(
        HttpClient client,
        Guid agreementId,
        int number,
        Guid headingId,
        string title,
        string body = "Cuerpo de la cláusula.",
        string? economicComparisonNote = null)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/agreements/{agreementId}/clauses", UriKind.Relative),
            new ClauseRequest(
                number,
                title,
                null,
                body,
                headingId,
                economicComparisonNote,
                false,
                null));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var clause = await response.Content.ReadFromJsonAsync<ClauseResponse>();
        clause.ShouldNotBeNull();
        return clause!;
    }

    private static async Task PublishAsync(HttpClient client, Guid agreementId)
    {
        var response = await client.PostAsync(
            new Uri($"/agreements/{agreementId}/publish", UriKind.Relative), content: null);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    private static async Task<(AgreementDetail Agreement, ClauseResponse Clause)> CreatePublishedAsync(
        HttpClient client,
        DateOnly validFrom,
        Guid headingId)
    {
        var agreement = await CreateAgreementAsync(client, validFrom);
        var clause = await AddClauseAsync(
            client,
            agreement.Id,
            1,
            headingId,
            $"Cláusula {validFrom:yyyy-MM-dd}");
        await PublishAsync(client, agreement.Id);
        return (agreement, clause);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem is not null && problem.TryGetValue("code", out var code)
            ? code?.ToString()
            : null;
    }
}
