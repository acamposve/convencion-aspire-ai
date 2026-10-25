using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Convenciones.Api.Endpoints;
using Convenciones.Agreements;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// The agreement authoring contract: identity, integer clause numbering, mandatory classification,
/// publication as a recorded act, and per-company isolation.
/// </summary>
/// <remarks>
/// The database is asserted alongside the API. Criterion 1 explicitly asks for proof that the
/// uniqueness constraint — not only the handler — refuses a duplicate, and criterion 5 requires the
/// publication row itself to carry the actor and timestamp.
/// </remarks>
[Collection(AgreementsCollection.Name)]
public sealed class AgreementTests(AgreementFixture fixture) : IDisposable
{
    private readonly List<ApiFactory> _hosts = [];

    [Fact]
    public async Task Duplicate_company_and_start_date_are_refused_by_the_api_and_the_database()
    {
        var from = new DateOnly(2026, 1, 1);
        using var client = ClientFor(AgreementFixture.EditorA);

        await CreateAgreementAsync(client, from);

        var duplicate = await client.PostAsJsonAsync(
            new Uri("/agreements", UriKind.Relative),
            new CreateAgreementRequest(null, from, null, "Nacional"));

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(duplicate)).ShouldBe("conflict");
        (await fixture.AgreementCountForDateAsync(AgreementFixture.CompanyA, from)).ShouldBe(1);

        // The handler could be wrong; the database constraint is the second line of defence.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "insert into app.agreement (company_id, valid_from, scope) values (@company, @from, 'Nacional')",
            connection);
        command.Parameters.AddWithValue("company", AgreementFixture.CompanyA);
        command.Parameters.AddWithValue("from", from);

        PostgresException? error = null;

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            error = exception;
        }

        error.ShouldNotBeNull("the unique index must refuse a duplicate even when the handler is bypassed");
        error!.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("agreement_company_valid_from_key");
        (await fixture.AgreementCountForDateAsync(AgreementFixture.CompanyA, from)).ShouldBe(1);
    }

    [Fact]
    public async Task Clause_numbers_are_integers_ordered_numerically_and_unique_within_the_agreement()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var agreement = await CreateAgreementAsync(client, new DateOnly(2026, 2, 1));
        var heading = await fixture.HeadingIdAsync("Salarios");

        await AddClauseAsync(client, agreement.Id, 10, heading);
        await AddClauseAsync(client, agreement.Id, 2, heading);

        var clauses = await client.GetFromJsonAsync<ClauseListResponse>(
            new Uri($"/agreements/{agreement.Id}/clauses", UriKind.Relative));

        clauses.ShouldNotBeNull();
        clauses!.Items.Select(clause => clause.Number).ShouldBe(new[] { 2, 10 });
        clauses.Total.ShouldBe(2);

        var duplicate = await client.PostAsJsonAsync(
            new Uri($"/agreements/{agreement.Id}/clauses", UriKind.Relative),
            ClauseRequest(2, heading));

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(duplicate)).ShouldBe("conflict");

        var zero = await client.PostAsJsonAsync(
            new Uri($"/agreements/{agreement.Id}/clauses", UriKind.Relative),
            ClauseRequest(0, heading));

        zero.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(zero)).ShouldBe("validation_failed");
        (await fixture.ClauseCountAsync(agreement.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task A_clause_without_a_heading_is_refused()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var agreement = await CreateAgreementAsync(client, new DateOnly(2026, 3, 1));
        var heading = await fixture.HeadingIdAsync("Salarios");

        // The heading field is absent from the JSON entirely.
        var missing = await client.PostAsJsonAsync(
            new Uri($"/agreements/{agreement.Id}/clauses", UriKind.Relative),
            new
            {
                number = 1,
                title = "Cláusula sin clasificar",
                summary = (string?)null,
                body = "Texto.",
                economicComparisonNote = (string?)null,
                isExcludedFromCompilation = false,
                exclusionNote = (string?)null,
            });

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(missing)).ShouldBe("validation_failed");

        var unknown = await client.PostAsJsonAsync(
            new Uri($"/agreements/{agreement.Id}/clauses", UriKind.Relative),
            ClauseRequest(1, Guid.NewGuid()));

        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(unknown)).ShouldBe("validation_failed");
        (await fixture.ClauseCountAsync(agreement.Id)).ShouldBe(0);

        var accepted = await AddClauseAsync(client, agreement.Id, 1, heading);
        accepted.HeadingId.ShouldBe(heading);
        accepted.HeadingName.ShouldBe("Salarios");
    }

    [Fact]
    public async Task Publishing_without_the_capability_is_forbidden_in_the_api_and_at_the_database()
    {
        using var editor = ClientFor(AgreementFixture.EditorA);
        using var transcriptor = ClientFor(AgreementFixture.Transcriptor);
        var agreement = await CreateAgreementAsync(editor, new DateOnly(2026, 4, 1));
        await AddClauseAsync(editor, agreement.Id, 1, await fixture.HeadingIdAsync("Salarios"));

        var before = await fixture.AuditCountAsync(agreement.Id);
        var refused = await transcriptor.PostAsync(
            new Uri($"/agreements/{agreement.Id}/publish", UriKind.Relative), content: null);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(refused)).ShouldBe("forbidden");

        var row = await fixture.AgreementRowAsync(agreement.Id);
        row.State.ShouldBe("Draft");
        row.PublishedAt.ShouldBeNull();
        row.PublishedBy.ShouldBeNull();
        (await fixture.AuditCountAsync(agreement.Id)).ShouldBe(before);

        // A transcriptor has agreement.write but not agreement.publish. The update policy lets the
        // row through; the database guard must still refuse the state change.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var claims = $$"""
            {"sub":"{{AgreementFixture.Transcriptor}}","role":"authenticated"}
            """;

        await using (var setClaims = new NpgsqlCommand(
            "select set_config('request.jwt.claims', @claims, false)", connection))
        {
            setClaims.Parameters.AddWithValue("claims", claims.Trim());
            await setClaims.ExecuteNonQueryAsync();
        }

        await using (var setRole = new NpgsqlCommand("set role app_api", connection))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        PostgresException? databaseRefusal = null;

        try
        {
            await using var update = new NpgsqlCommand(
                """
                update app.agreement
                set state = 'Published', published_at = now(), published_by = @actor
                where id = @id
                """, connection);
            update.Parameters.AddWithValue("actor", AgreementFixture.Transcriptor);
            update.Parameters.AddWithValue("id", agreement.Id);
            await update.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            databaseRefusal = exception;
        }
        finally
        {
            await using var resetRole = new NpgsqlCommand("reset role", connection);
            await resetRole.ExecuteNonQueryAsync();
            await using var resetClaims = new NpgsqlCommand(
                "select set_config('request.jwt.claims', '', false)", connection);
            await resetClaims.ExecuteNonQueryAsync();
        }

        databaseRefusal.ShouldNotBeNull("the database must refuse a publication without the capability");
        databaseRefusal!.SqlState.ShouldBe("42501");
        databaseRefusal.MessageText.ShouldContain("agreement.publish");
        (await fixture.AgreementRowAsync(agreement.Id)).State.ShouldBe("Draft");
    }

    [Fact]
    public async Task Publishing_records_the_actor_and_timestamp_and_unpublishing_clears_them()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var agreement = await CreateAgreementAsync(client, new DateOnly(2026, 5, 1));
        await AddClauseAsync(client, agreement.Id, 1, await fixture.HeadingIdAsync("Salarios"));

        var before = await fixture.AuditCountAsync(agreement.Id);
        var published = await client.PostAsync(
            new Uri($"/agreements/{agreement.Id}/publish", UriKind.Relative), content: null);

        published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
        var publishedDetail = await published.Content.ReadFromJsonAsync<AgreementDetail>();
        publishedDetail.ShouldNotBeNull();
        publishedDetail!.State.ShouldBe("Published");
        publishedDetail.PublishedAt.ShouldNotBeNull();

        var row = await fixture.AgreementRowAsync(agreement.Id);
        row.State.ShouldBe("Published");
        row.PublishedAt.ShouldNotBeNull();
        row.PublishedBy.ShouldBe(AgreementFixture.EditorA);

        var afterPublish = await fixture.AuditCountAsync(agreement.Id);
        afterPublish.ShouldBe(before + 1, "publishing must be audited exactly once");
        var changes = await fixture.AuditChangesAsync(agreement.Id);
        changes.Last().ShouldContain("PublishedAt");
        changes.Last().ShouldContain("State");

        var unpublish = await client.PostAsync(
            new Uri($"/agreements/{agreement.Id}/unpublish", UriKind.Relative), content: null);

        unpublish.StatusCode.ShouldBe(HttpStatusCode.OK, await unpublish.Content.ReadAsStringAsync());
        var unpublished = await unpublish.Content.ReadFromJsonAsync<AgreementDetail>();
        unpublished!.State.ShouldBe("Draft");
        unpublished.PublishedAt.ShouldBeNull();

        var cleared = await fixture.AgreementRowAsync(agreement.Id);
        cleared.State.ShouldBe("Draft");
        cleared.PublishedAt.ShouldBeNull();
        cleared.PublishedBy.ShouldBeNull();
        (await fixture.AuditCountAsync(agreement.Id)).ShouldBe(afterPublish + 1);

        var empty = await CreateAgreementAsync(client, new DateOnly(2026, 5, 15));
        var refused = await client.PostAsync(
            new Uri($"/agreements/{empty.Id}/publish", UriKind.Relative), content: null);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(refused)).ShouldBe("invalid_state");
        (await fixture.AgreementRowAsync(empty.Id)).State.ShouldBe("Draft");
    }

    [Fact]
    public async Task The_composed_title_follows_the_company_name_and_is_not_stored()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var agreement = await CreateAgreementAsync(
            client,
            new DateOnly(2026, 6, 1),
            new DateOnly(2028, 12, 31));

        var detail = await GetDetailAsync(client, agreement.Id);
        detail.Title.ShouldContain("Agreements Company A");
        detail.Title.ShouldContain("2026");
        detail.Title.ShouldContain("2028");

        var list = await client.GetFromJsonAsync<AgreementListResponse>(
            new Uri("/agreements?page=1&pageSize=100", UriKind.Relative));
        list.ShouldNotBeNull();
        list!.Items.Single(item => item.Id == agreement.Id).Title.ShouldBe(detail.Title);

        (await fixture.AgreementColumnCountAsync("title", "number"))
            .ShouldBe(0, "Q-1 removed the stored title and number");

        var originalName = await fixture.CompanyNameAsync(AgreementFixture.CompanyA);

        try
        {
            await fixture.SetCompanyNameAsync(AgreementFixture.CompanyA, "Renamed Agreement Co");
            var renamed = await GetDetailAsync(client, agreement.Id);
            renamed.Title.ShouldContain("Renamed Agreement Co");
            renamed.Title.ShouldNotContain(originalName);
        }
        finally
        {
            await fixture.SetCompanyNameAsync(AgreementFixture.CompanyA, originalName);
        }
    }

    [Fact]
    public async Task Another_company_cannot_read_or_write_the_agreement()
    {
        using var editorA = ClientFor(AgreementFixture.EditorA);
        using var editorB = ClientFor(AgreementFixture.EditorB);
        var agreement = await CreateAgreementAsync(editorA, new DateOnly(2026, 7, 1));
        await AddClauseAsync(editorA, agreement.Id, 1, await fixture.HeadingIdAsync("Salarios"));

        (await editorB.GetAsync(new Uri($"/agreements/{agreement.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var patch = await SendWithIfMatchAsync(
            editorB,
            HttpMethod.Patch,
            $"/agreements/{agreement.Id}",
            new UpdateAgreementRequest(new DateOnly(2026, 7, 1), null, "Intento"),
            agreement.RowVersion);
        patch.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await editorB.PostAsync(
                new Uri($"/agreements/{agreement.Id}/publish", UriKind.Relative), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await editorB.GetAsync(
                new Uri($"/agreements/{agreement.Id}/clauses", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var addClause = await editorB.PostAsJsonAsync(
            new Uri($"/agreements/{agreement.Id}/clauses", UriKind.Relative),
            ClauseRequest(2, await fixture.HeadingIdAsync("Salarios")));
        addClause.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var createIntoA = await editorB.PostAsJsonAsync(
            new Uri("/agreements", UriKind.Relative),
            new CreateAgreementRequest(
                AgreementFixture.CompanyA, new DateOnly(2026, 7, 15), null, "Ajeno"));
        createIntoA.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(createIntoA)).ShouldBe("forbidden");

        var row = await fixture.AgreementRowAsync(agreement.Id);
        row.Scope.ShouldBe("Nacional");
        row.State.ShouldBe("Draft");
        (await fixture.ClauseCountAsync(agreement.Id)).ShouldBe(1);

        // The API scoping is the first line; the database policy is the second. As company B, the
        // agreement and its clauses must be invisible even with the read capability.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var setClaims = new NpgsqlCommand(
            "select set_config('request.jwt.claims', @claims, false)", connection))
        {
            setClaims.Parameters.AddWithValue(
                "claims",
                $$"""{"sub":"{{AgreementFixture.EditorB}}","role":"authenticated"}""");
            await setClaims.ExecuteNonQueryAsync();
        }

        await using (var setRole = new NpgsqlCommand("set role app_api", connection))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        try
        {
            await using var visible = new NpgsqlCommand(
                """
                select
                    (select count(*) from app.agreement where id = @id)::int,
                    (select count(*) from app.agreement_clause where agreement_id = @id)::int
                """, connection);
            visible.Parameters.AddWithValue("id", agreement.Id);
            await using var reader = await visible.ExecuteReaderAsync();
            await reader.ReadAsync();
            reader.GetInt32(0).ShouldBe(0, "RLS must hide another company's agreement");
            reader.GetInt32(1).ShouldBe(0, "RLS must hide its clauses too");
        }
        finally
        {
            await using var resetRole = new NpgsqlCommand("reset role", connection);
            await resetRole.ExecuteNonQueryAsync();
            await using var resetClaims = new NpgsqlCommand(
                "select set_config('request.jwt.claims', '', false)", connection);
            await resetClaims.ExecuteNonQueryAsync();
        }

        var listB = await editorB.GetFromJsonAsync<AgreementListResponse>(
            new Uri("/agreements?page=1&pageSize=100", UriKind.Relative));
        listB.ShouldNotBeNull();
        listB!.Total.ShouldBe(await fixture.AgreementCountAsync(AgreementFixture.CompanyB));
        listB.Items.Select(item => item.Id).ShouldNotContain(agreement.Id);

        (await editorA.GetAsync(new Uri($"/agreements/{agreement.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Header_updates_require_the_current_row_version_and_respect_identity()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var agreement = await CreateAgreementAsync(client, new DateOnly(2026, 8, 1));
        var other = await CreateAgreementAsync(client, new DateOnly(2026, 8, 15));
        var detail = await GetDetailAsync(client, agreement.Id);

        var missing = await client.PatchAsJsonAsync(
            new Uri($"/agreements/{agreement.Id}", UriKind.Relative),
            new UpdateAgreementRequest(new DateOnly(2026, 8, 1), null, "Regional"));

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(missing)).ShouldBe("validation_failed");

        var staleVersion = detail.RowVersion + 1;
        var stale = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/agreements/{agreement.Id}",
            new UpdateAgreementRequest(new DateOnly(2026, 8, 1), null, "Regional"),
            staleVersion);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(stale)).ShouldBe("concurrent_modification");

        var current = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/agreements/{agreement.Id}",
            new UpdateAgreementRequest(new DateOnly(2026, 8, 1), null, "Regional"),
            detail.RowVersion);
        current.StatusCode.ShouldBe(HttpStatusCode.OK, await current.Content.ReadAsStringAsync());
        var updated = await current.Content.ReadFromJsonAsync<AgreementDetail>();
        updated.ShouldNotBeNull();
        updated!.Scope.ShouldBe("Regional");
        updated.RowVersion.ShouldNotBe(detail.RowVersion);

        var collision = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/agreements/{agreement.Id}",
            new UpdateAgreementRequest(other.ValidFrom, null, "Collision"),
            updated.RowVersion);
        collision.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(collision)).ShouldBe("conflict");

        var row = await fixture.AgreementRowAsync(agreement.Id);
        row.Scope.ShouldBe("Regional", "a failed unique update must not partially apply");
        row.ValidFrom.ShouldBe(new DateOnly(2026, 8, 1));
    }

    [Fact]
    public async Task Clause_update_reclassify_and_delete_round_trip()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var agreement = await CreateAgreementAsync(client, new DateOnly(2026, 9, 1));
        var salarios = await fixture.HeadingIdAsync("Salarios");
        var vacaciones = await fixture.HeadingIdAsync("Vacaciones");
        var clause = await AddClauseAsync(client, agreement.Id, 1, salarios);

        var updatedResponse = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/agreements/{agreement.Id}/clauses/{clause.Id}",
            new ClauseRequest(3, "Título actualizado", "Resumen", "Cuerpo actualizado", vacaciones, null, false, null),
            clause.RowVersion);

        updatedResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await updatedResponse.Content.ReadAsStringAsync());
        var updated = await updatedResponse.Content.ReadFromJsonAsync<ClauseResponse>();
        updated.ShouldNotBeNull();
        updated!.Number.ShouldBe(3);
        updated.Title.ShouldBe("Título actualizado");
        updated.HeadingId.ShouldBe(vacaciones);
        updated.HeadingName.ShouldBe("Vacaciones");

        var reclassifiedResponse = await SendWithIfMatchAsync(
            client,
            HttpMethod.Put,
            $"/agreements/{agreement.Id}/clauses/{clause.Id}/heading",
            new ReclassifyClauseRequest(salarios),
            updated.RowVersion);

        reclassifiedResponse.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await reclassifiedResponse.Content.ReadAsStringAsync());
        var reclassified = await reclassifiedResponse.Content.ReadFromJsonAsync<ClauseResponse>();
        reclassified.ShouldNotBeNull();
        reclassified!.HeadingId.ShouldBe(salarios);
        reclassified.HeadingName.ShouldBe("Salarios");

        var deleted = await SendWithIfMatchAsync(
            client,
            HttpMethod.Delete,
            $"/agreements/{agreement.Id}/clauses/{clause.Id}",
            content: (object?)null,
            reclassified.RowVersion);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync(new Uri($"/agreements/{agreement.Id}/clauses/{clause.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.ClauseCountAsync(agreement.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task Annex_metadata_round_trips_and_names_are_unique_ignoring_case()
    {
        using var client = ClientFor(AgreementFixture.EditorA);
        var agreement = await CreateAgreementAsync(client, new DateOnly(2026, 10, 1));

        var created = await client.PostAsJsonAsync(
            new Uri($"/agreements/{agreement.Id}/annexes", UriKind.Relative),
            new AnnexRequest("Anexo A", "Descripción", "Cuerpo", null, 10));

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var annex = await created.Content.ReadFromJsonAsync<AnnexResponse>();
        annex.ShouldNotBeNull();

        var duplicate = await client.PostAsJsonAsync(
            new Uri($"/agreements/{agreement.Id}/annexes", UriKind.Relative),
            new AnnexRequest("anexo a", null, null, null, 20));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(duplicate)).ShouldBe("conflict");

        var updated = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/agreements/{agreement.Id}/annexes/{annex!.Id}",
            new AnnexRequest("Anexo B", null, "Nuevo cuerpo", null, 5),
            annex.RowVersion);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        var renamed = await updated.Content.ReadFromJsonAsync<AnnexResponse>();
        renamed.ShouldNotBeNull();
        renamed!.Name.ShouldBe("Anexo B");
        renamed.SortOrder.ShouldBe(5);

        var deleted = await SendWithIfMatchAsync(
            client,
            HttpMethod.Delete,
            $"/agreements/{agreement.Id}/annexes/{annex.Id}",
            content: (object?)null,
            renamed.RowVersion);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var list = await client.GetFromJsonAsync<AnnexListResponse>(
            new Uri($"/agreements/{agreement.Id}/annexes", UriKind.Relative));
        list.ShouldNotBeNull();
        list!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_is_company_scoped_paged_and_filterable_and_reports_the_composed_title()
    {
        using var editorA = ClientFor(AgreementFixture.EditorA);
        using var editorB = ClientFor(AgreementFixture.EditorB);
        var baseline = await fixture.AgreementCountAsync(AgreementFixture.CompanyA);

        var first = await CreateAgreementAsync(editorA, new DateOnly(2026, 11, 1));
        var second = await CreateAgreementAsync(editorA, new DateOnly(2026, 11, 2));
        var third = await CreateAgreementAsync(editorA, new DateOnly(2026, 11, 3));
        var companyB = await CreateAgreementAsync(editorB, new DateOnly(2026, 12, 1));

        var pageOne = await editorA.GetFromJsonAsync<AgreementListResponse>(
            new Uri("/agreements?page=1&pageSize=2", UriKind.Relative));
        var pageTwo = await editorA.GetFromJsonAsync<AgreementListResponse>(
            new Uri("/agreements?page=2&pageSize=2", UriKind.Relative));

        pageOne.ShouldNotBeNull();
        pageTwo.ShouldNotBeNull();
        pageOne!.Total.ShouldBe(baseline + 3);
        pageOne.Items.Count.ShouldBe(2);
        pageOne.Items.Select(item => item.Id).ShouldNotContain(companyB.Id);
        pageOne.Items.Select(item => item.Id)
            .Intersect(pageTwo!.Items.Select(item => item.Id))
            .ShouldBeEmpty();

        var all = await editorA.GetFromJsonAsync<AgreementListResponse>(
            new Uri("/agreements?page=1&pageSize=100", UriKind.Relative));
        all.ShouldNotBeNull();
        all!.Total.ShouldBe(baseline + 3);
        all.Items.Select(item => item.Id).ShouldContain(first.Id);
        all.Items.Select(item => item.Id).ShouldContain(second.Id);
        all.Items.Select(item => item.Id).ShouldContain(third.Id);
        all.Items.Select(item => item.Id).ShouldNotContain(companyB.Id);
        all.Items.ShouldAllBe(item => item.CompanyId == AgreementFixture.CompanyA);

        var drafts = await editorA.GetFromJsonAsync<AgreementListResponse>(
            new Uri("/agreements?state=Draft&page=1&pageSize=100", UriKind.Relative));
        drafts.ShouldNotBeNull();
        drafts!.Items.ShouldAllBe(item => item.State == "Draft");
        drafts.Total.ShouldBe(
            await fixture.AgreementCountAsync(AgreementFixture.CompanyA, "Draft"));

        var search = await editorA.GetFromJsonAsync<AgreementListResponse>(
            new Uri(
                $"/agreements?q={Uri.EscapeDataString("Agreements Company A")}&page=1&pageSize=100",
                UriKind.Relative));
        search.ShouldNotBeNull();
        search!.Items.Select(item => item.Id).ShouldContain(first.Id);
        search.Items.ShouldAllBe(item => item.CompanyName == "Agreements Company A");

        var listB = await editorB.GetFromJsonAsync<AgreementListResponse>(
            new Uri("/agreements?page=1&pageSize=100", UriKind.Relative));
        listB.ShouldNotBeNull();
        listB!.Items.Select(item => item.Id).ShouldNotContain(first.Id);
        listB.Total.ShouldBe(await fixture.AgreementCountAsync(AgreementFixture.CompanyB));
    }

    [Fact]
    public async Task Heading_taxonomy_read_hides_excluded_headings_by_default()
    {
        using var client = ClientFor(AgreementFixture.EditorA);

        var withExcluded = await client.GetFromJsonAsync<HeadingGroupResponse[]>(
            new Uri("/comparison/headings?includeExcluded=true", UriKind.Relative));
        withExcluded.ShouldNotBeNull();
        var allNames = withExcluded!
            .SelectMany(group => group.Headings)
            .Select(heading => heading.Name)
            .ToArray();

        allNames.ShouldContain("Salarios");
        allNames.ShouldContain("Vacaciones");
        allNames.ShouldContain("No Compara");

        var withoutExcluded = await client.GetFromJsonAsync<HeadingGroupResponse[]>(
            new Uri("/comparison/headings", UriKind.Relative));
        withoutExcluded.ShouldNotBeNull();
        var visibleNames = withoutExcluded!
            .SelectMany(group => group.Headings)
            .Select(heading => heading.Name)
            .ToArray();

        visibleNames.ShouldContain("Salarios");
        visibleNames.ShouldContain("Vacaciones");
        visibleNames.ShouldNotContain("No Compara");

        var invalid = await client.GetAsync(
            new Uri("/comparison/headings?groupIds=not-a-guid", UriKind.Relative));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(invalid)).ShouldBe("validation_failed");
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
        DateOnly? validTo = null,
        Guid? companyId = null,
        string scope = "Nacional")
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/agreements", UriKind.Relative),
            new CreateAgreementRequest(companyId, validFrom, validTo, scope));

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
        Guid headingId)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/agreements/{agreementId}/clauses", UriKind.Relative),
            ClauseRequest(number, headingId, $"Cláusula {number}"));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var clause = await response.Content.ReadFromJsonAsync<ClauseResponse>();
        clause.ShouldNotBeNull();
        return clause!;
    }

    private static async Task<AgreementDetail> GetDetailAsync(HttpClient client, Guid agreementId)
    {
        var response = await client.GetAsync(
            new Uri($"/agreements/{agreementId}", UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var detail = await response.Content.ReadFromJsonAsync<AgreementDetail>();
        detail.ShouldNotBeNull();
        return detail!;
    }

    private static ClauseRequest ClauseRequest(int number, Guid headingId, string? title = null) =>
        new(
            number,
            title ?? $"Cláusula {number}",
            "Resumen",
            "Cuerpo de la cláusula.",
            headingId,
            null,
            false,
            null);

    private static async Task<HttpResponseMessage> SendWithIfMatchAsync<T>(
        HttpClient client,
        HttpMethod method,
        string path,
        T content,
        uint rowVersion)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        if (content is not null)
        {
            request.Content = JsonContent.Create(content);
        }

        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{rowVersion}\""));
        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem is not null && problem.TryGetValue("code", out var code)
            ? code?.ToString()
            : null;
    }
}
