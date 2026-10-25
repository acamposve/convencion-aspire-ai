using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Convenciones.Api.Endpoints;
using Convenciones.Comparison;
using Convenciones.Legal;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// The legal library contract: one labour statute, integer article numbering, an article that cannot
/// exist without its heading, and reference data that every company reads.
/// </summary>
/// <remarks>
/// The database is asserted alongside the API wherever a rule is enforced twice. Criterion 1 asks for
/// proof that the <b>partial unique index</b> is what refuses a second labour statute, so that test
/// bypasses the handler and inserts directly; criterion 3 relies on a CHECK constraint that is
/// asserted the same way.
/// </remarks>
[Collection(StatutesCollection.Name)]
public sealed class StatuteTests(StatuteFixture fixture) : IDisposable
{
    private readonly List<ApiFactory> _hosts = [];

    [Fact]
    public async Task A_second_labour_law_is_refused_by_the_api_and_by_the_partial_unique_index()
    {
        using var client = ClientFor(StatuteFixture.EditorA);

        if (await fixture.LabourLawCountAsync() == 0)
        {
            var first = await client.PostAsJsonAsync(
                new Uri("/statutes", UriKind.Relative),
                new CreateStatuteRequest(
                    "LabourLaw",
                    "Código del Trabajo",
                    new DateOnly(2012, 5, 7),
                    "Gaceta Oficial 6.076",
                    null));

            first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        }

        var second = await client.PostAsJsonAsync(
            new Uri("/statutes", UriKind.Relative),
            new CreateStatuteRequest("LabourLaw", "Segunda Ley del Trabajo", null, null, null));

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(second);
        problem.Code.ShouldBe("conflict");
        problem.Detail.ShouldNotBeNull();
        problem.Detail!.ShouldContain(StatuteEndpoints.SingleLabourLawIndex);
        (await fixture.LabourLawCountAsync()).ShouldBe(1);

        // The rule spans rows, so no aggregate can evaluate it. Bypassing the API proves the index —
        // not the handler — refuses the second row, and that the refusal names the index.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        PostgresException? refusal = null;

        try
        {
            await using var insert = new NpgsqlCommand(
                "insert into app.statute (kind, name) values ('LabourLaw', 'Insertada Sin La Api')",
                connection);
            await insert.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            refusal = exception;
        }

        refusal.ShouldNotBeNull("the partial unique index must refuse a second LabourLaw statute");
        refusal!.SqlState.ShouldBe("23505");
        refusal.ConstraintName.ShouldBe(StatuteEndpoints.SingleLabourLawIndex);
        (await fixture.LabourLawCountAsync()).ShouldBe(1);

        // Only the labour statute is singular; other statutes are unrestricted.
        foreach (var name in new[] { "Ley Orgánica de Precios Justos", "Reglamento Parcial del Trabajo" })
        {
            var other = await client.PostAsJsonAsync(
                new Uri("/statutes", UriKind.Relative),
                new CreateStatuteRequest("OtherStatute", name, null, null, null));

            other.StatusCode.ShouldBe(HttpStatusCode.Created, await other.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Article_numbers_are_integers_unique_per_statute_and_ordered_numerically()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var statute = await CreateStatuteAsync(client, "OtherStatute", "Ley de Numeración");
        var sibling = await CreateStatuteAsync(client, "OtherStatute", "Ley de Numeración Paralela");

        await AddArticleAsync(client, statute.Id, 10, "Artículo 10 — Décimo");
        await AddArticleAsync(client, statute.Id, 2, "Artículo 2 — Segundo");
        await AddArticleAsync(client, statute.Id, 1, "Artículo 1 — Primero");

        var list = await client.GetFromJsonAsync<StatuteArticleListResponse>(
            new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative));

        list.ShouldNotBeNull();
        list!.Items.Select(article => article.Number).ShouldBe(new[] { 1, 2, 10 });
        list.Total.ShouldBe(3);

        // Numeric order must be visible on the wire, not only in the C# model: the legacy column was
        // a varchar and sorted 1, 10, 100, 11, 2.
        var json = await client.GetStringAsync(new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative));

        using (var document = JsonDocument.Parse(json))
        {
            var numbers = document.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("number"))
                .ToArray();

            numbers.ShouldAllBe(number => number.ValueKind == JsonValueKind.Number);
            numbers.Select(number => number.GetInt32()).ShouldBe(new[] { 1, 2, 10 });
        }

        var duplicate = await client.PostAsJsonAsync(
            new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative),
            ArticleRequest(2, "Artículo 2 — Duplicado"));

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(duplicate)).Code.ShouldBe("conflict");
        (await fixture.ArticleCountAsync(statute.Id)).ShouldBe(3);

        var zero = await client.PostAsJsonAsync(
            new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative),
            ArticleRequest(0, "Artículo 0 — Imposible"));

        zero.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(zero)).Code.ShouldBe("validation_failed");
        (await fixture.ArticleCountAsync(statute.Id)).ShouldBe(3);

        // The same number in a different statute is a different article.
        var other = await AddArticleAsync(client, sibling.Id, 2, "Artículo 2 — Otra ley");
        other.Number.ShouldBe(2);

        // And the uniqueness is the database's as well.
        PostgresException? refusal = null;

        try
        {
            await using var connection = await OpenAsync();
            await using var insert = new NpgsqlCommand(
                """
                insert into app.statute_article (statute_id, number_int, title, body)
                values (@statute, 2, 'Artículo duplicado', 'Cuerpo')
                """, connection);
            insert.Parameters.AddWithValue("statute", statute.Id);
            await insert.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            refusal = exception;
        }

        refusal.ShouldNotBeNull();
        refusal!.SqlState.ShouldBe("23505");
        refusal.ConstraintName.ShouldBe(StatuteEndpoints.ArticleNumberIndex);
    }

    [Fact]
    public async Task An_article_without_its_heading_is_refused()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var statute = await CreateStatuteAsync(client, "OtherStatute", "Ley sin Encabezados");

        // The heading field is absent from the JSON entirely.
        var missing = await client.PostAsJsonAsync(
            new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative),
            new
            {
                number = 1,
                summary = (string?)null,
                body = "Texto del artículo.",
                headingId = (Guid?)null,
                economicComparisonNote = (string?)null,
            });

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(missing)).Code.ShouldBe("validation_failed");

        var blank = await client.PostAsJsonAsync(
            new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative),
            ArticleRequest(1, "   "));

        blank.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(blank)).Code.ShouldBe("validation_failed");

        var noBody = await client.PostAsJsonAsync(
            new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative),
            new StatuteArticleRequest(1, "Artículo 1 — Sin cuerpo", null, "  ", null, null));

        noBody.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.ArticleCountAsync(statute.Id)).ShouldBe(0);

        // The database holds the same rule, so bypassing the API cannot store the unusable row.
        PostgresException? refusal = null;

        try
        {
            await using var connection = await OpenAsync();
            await using var insert = new NpgsqlCommand(
                """
                insert into app.statute_article (statute_id, number_int, title, body)
                values (@statute, 1, '   ', 'Cuerpo')
                """, connection);
            insert.Parameters.AddWithValue("statute", statute.Id);
            await insert.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            refusal = exception;
        }

        refusal.ShouldNotBeNull("statute_article_title_not_blank must refuse a blank heading");
        refusal!.SqlState.ShouldBe("23514");
        refusal.ConstraintName.ShouldBe("statute_article_title_not_blank");

        // What is optional is the CLASSIFICATION (A-12), not the article's own heading.
        var unclassified = await AddArticleAsync(client, statute.Id, 1, "Artículo 1 — Objeto");
        unclassified.HeadingId.ShouldBeNull();

        var unknownHeading = await client.PostAsJsonAsync(
            new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative),
            ArticleRequest(2, "Artículo 2 — Clasificación inventada", headingId: Guid.NewGuid()));

        unknownHeading.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(unknownHeading)).Code.ShouldBe("validation_failed");

        // An empty Guid is the client failing to send null, not an unclassified article.
        var emptyHeading = await client.PostAsJsonAsync(
            new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative),
            ArticleRequest(3, "Artículo 3 — Clasificación vacía", headingId: Guid.Empty));

        emptyHeading.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(emptyHeading)).Code.ShouldBe("validation_failed");
        (await fixture.ArticleCountAsync(statute.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Statutes_are_readable_by_a_user_of_any_company_and_carry_no_company_predicate()
    {
        using var editorA = ClientFor(StatuteFixture.EditorA);
        var statute = await CreateStatuteAsync(editorA, "OtherStatute", "Ley Interempresarial");
        await AddArticleAsync(editorA, statute.Id, 1, "Artículo 1 — Común a todas las empresas");

        // A user of company B reads a statute created by company A, with no special capability.
        using var editorB = ClientFor(StatuteFixture.EditorB);
        var listB = await editorB.GetFromJsonAsync<StatuteListResponse>(
            new Uri("/statutes?page=1&pageSize=100", UriKind.Relative));

        listB.ShouldNotBeNull();
        listB!.Items.Select(item => item.Id).ShouldContain(statute.Id);
        listB.Total.ShouldBe(await fixture.StatuteCountAsync());

        (await editorB.GetAsync(new Uri($"/statutes/{statute.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var articlesB = await editorB.GetFromJsonAsync<StatuteArticleListResponse>(
            new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative));
        articlesB.ShouldNotBeNull();
        articlesB!.Items.Single().Title.ShouldBe("Artículo 1 — Común a todas las empresas");

        // A user with no company at all reads the same library: the reference data does not depend on
        // a company membership.
        using var withoutCompany = ClientFor(StatuteFixture.EditorWithoutCompany);
        var listNone = await withoutCompany.GetFromJsonAsync<StatuteListResponse>(
            new Uri("/statutes?page=1&pageSize=100", UriKind.Relative));

        listNone.ShouldNotBeNull();
        listNone!.Items.Select(item => item.Id).ShouldContain(statute.Id);
        listNone.Total.ShouldBe(listB.Total);

        // There is no company column to filter on, which is why no tenant predicate may be applied.
        (await fixture.CompanyColumnCountAsync()).ShouldBe(0);

        // The database policy agrees: a company-B caller sees every statute, not only "its own".
        var visible = await ScalarAsAsync(
            StatuteFixture.EditorB,
            "select count(*) from app.statute");

        visible.ShouldBe(await fixture.StatuteCountAsync());
    }

    [Fact]
    public async Task Writes_require_statute_write_in_the_api_and_at_the_database()
    {
        using var editor = ClientFor(StatuteFixture.EditorA);
        var statute = await CreateStatuteAsync(editor, "OtherStatute", "Ley de Solo Lectura");
        var article = await AddArticleAsync(editor, statute.Id, 1, "Artículo 1 — Solo lectura");

        using var negotiator = ClientFor(StatuteFixture.Negotiator);

        var create = await negotiator.PostAsJsonAsync(
            new Uri("/statutes", UriKind.Relative),
            new CreateStatuteRequest("OtherStatute", "Intentona de Creación", null, null, null));

        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await ReadProblemAsync(create);
        problem.Code.ShouldBe("forbidden");
        problem.Detail.ShouldNotBeNull();
        problem.Detail!.ShouldContain("statute.write");

        (await negotiator.PatchAsJsonAsync(
                new Uri($"/statutes/{statute.Id}", UriKind.Relative),
                PatchStatute(name: Str("Renombrada por un lector"))))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await negotiator.PostAsJsonAsync(
                new Uri($"/statutes/{statute.Id}/articles", UriKind.Relative),
                ArticleRequest(2, "Artículo 2 — Ajeno")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await negotiator.DeleteAsync(
                new Uri($"/statutes/{statute.Id}/articles/{article.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await fixture.StatuteTextAsync(statute.Id, "name")).ShouldBe("Ley de Solo Lectura");
        (await fixture.ArticleCountAsync(statute.Id)).ShouldBe(1);

        // A negotiator holds statute.read and not statute.write, so the write policy refuses it.
        var refused = await TryInsertStatuteAsAsync(
            StatuteFixture.Negotiator, "Insertada Por Un Lector");

        refused.ShouldNotBeNull("RLS must refuse a statute written without statute.write");
        refused!.SqlState.ShouldBe("42501");

        // The same write succeeds for an editor, inside a transaction that is rolled back.
        var beforeEditor = await fixture.StatuteCountAsync();
        var allowed = await TryInsertStatuteAsAsync(
            StatuteFixture.EditorA, "Insertada Por Un Editor");

        allowed.ShouldBeNull();
        (await fixture.StatuteCountAsync()).ShouldBe(beforeEditor);
    }

    [Fact]
    public async Task Statute_updates_require_the_current_row_version_and_unique_names()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var statute = await CreateStatuteAsync(client, "OtherStatute", "Ley de Actualización");
        await CreateStatuteAsync(client, "OtherStatute", "Ley de Actualización II");

        var missing = await client.PatchAsJsonAsync(
            new Uri($"/statutes/{statute.Id}", UriKind.Relative),
            PatchStatute(name: Str("Ley Renombrada")));

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(missing)).Code.ShouldBe("validation_failed");

        var stale = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}",
            PatchStatute(name: Str("Ley Renombrada")),
            statute.RowVersion + 1);

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(stale)).Code.ShouldBe("concurrent_modification");
        (await fixture.StatuteTextAsync(statute.Id, "name")).ShouldBe("Ley de Actualización");

        var current = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}",
            PatchStatute(
                name: Str("Ley Renombrada"),
                publishedOn: On(new DateOnly(2020, 1, 1)),
                source: Str("Gaceta 1")),
            statute.RowVersion);

        current.StatusCode.ShouldBe(HttpStatusCode.OK, await current.Content.ReadAsStringAsync());
        var updated = await current.Content.ReadFromJsonAsync<StatuteDetail>();

        updated.ShouldNotBeNull();
        updated!.Name.ShouldBe("Ley Renombrada");
        updated.Kind.ShouldBe("OtherStatute");
        updated.PublishedOn.ShouldBe(new DateOnly(2020, 1, 1));
        updated.Source.ShouldBe("Gaceta 1");
        updated.RowVersion.ShouldNotBe(statute.RowVersion);

        // A rename onto a name another statute already holds is refused by statute_name_key, and the
        // conflict says which index refused it.
        var collision = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}",
            PatchStatute(name: Str("ley de actualización ii")),
            updated.RowVersion);

        collision.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(collision)).Detail!.ShouldContain(StatuteEndpoints.StatuteNameIndex);
        (await fixture.StatuteTextAsync(statute.Id, "name")).ShouldBe("Ley Renombrada");
    }

    [Fact]
    public async Task Article_text_round_trips_and_can_be_removed()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var statute = await CreateStatuteAsync(client, "OtherStatute", "Ley de Ciclo de Artículo");
        var heading = await fixture.HeadingIdAsync("Salarios");

        var article = await AddArticleAsync(
            client,
            statute.Id,
            5,
            "Artículo 5 — Salario mínimo",
            "El salario mínimo se fija por decreto.",
            heading);

        article.HeadingId.ShouldBe(heading);
        article.Number.ShouldBe(5);

        var fetched = await client.GetFromJsonAsync<StatuteArticleResponse>(
            new Uri($"/statutes/{statute.Id}/articles/{article.Id}", UriKind.Relative));

        fetched.ShouldNotBeNull();
        fetched!.Body.ShouldBe("El salario mínimo se fija por decreto.");
        fetched.Title.ShouldBe("Artículo 5 — Salario mínimo");

        var updatedResponse = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}/articles/{article.Id}",
            PatchArticle(
                number: Num(6),
                title: Str("Artículo 6 — Salario"),
                summary: Str("Resumen actualizado"),
                body: Str("Texto nuevo del artículo."),
                headingId: Id(null),
                economicNote: Str("Nota de comparación económica")),
            fetched.RowVersion);

        updatedResponse.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await updatedResponse.Content.ReadAsStringAsync());

        var updated = await updatedResponse.Content.ReadFromJsonAsync<StatuteArticleResponse>();

        updated.ShouldNotBeNull();
        updated!.Number.ShouldBe(6);
        updated.Title.ShouldBe("Artículo 6 — Salario");
        updated.EconomicComparisonNote.ShouldBe("Nota de comparación económica");

        // An explicit null clears the classification, which A-12 allows.
        updated.HeadingId.ShouldBeNull();

        var staleDelete = await SendWithIfMatchAsync(
            client,
            HttpMethod.Delete,
            $"/statutes/{statute.Id}/articles/{article.Id}",
            null,
            fetched.RowVersion);

        staleDelete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(staleDelete)).Code.ShouldBe("concurrent_modification");

        var deleted = await SendWithIfMatchAsync(
            client,
            HttpMethod.Delete,
            $"/statutes/{statute.Id}/articles/{article.Id}",
            null,
            updated.RowVersion);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync(
                new Uri($"/statutes/{statute.Id}/articles/{article.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.ArticleCountAsync(statute.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task Statute_and_article_writes_are_audited()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var statute = await CreateStatuteAsync(client, "OtherStatute", "Ley Auditada");
        var article = await AddArticleAsync(client, statute.Id, 1, "Artículo 1 — Auditado");

        (await fixture.AuditActionsAsync("statute", statute.Id.ToString())).ShouldBe(new[] { "created" });
        (await fixture.AuditModulesAsync("statute", statute.Id.ToString()))
            .ShouldBe(new[] { "legal_library" });

        (await fixture.AuditActionsAsync("statute_article", article.Id.ToString()))
            .ShouldBe(new[] { "created" });
        (await fixture.AuditModulesAsync("statute_article", article.Id.ToString()))
            .ShouldBe(new[] { "legal_library" });

        var detail = await client.GetFromJsonAsync<StatuteDetail>(
            new Uri($"/statutes/{statute.Id}", UriKind.Relative));

        detail.ShouldNotBeNull();

        var updated = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}",
            PatchStatute(name: Str("Ley Auditada y Renombrada")),
            detail!.RowVersion);

        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());

        (await fixture.AuditActionsAsync("statute", statute.Id.ToString()))
            .ShouldBe(new[] { "created", "updated" });

        var refreshed = await updated.Content.ReadFromJsonAsync<StatuteDetail>();

        var removed = await SendWithIfMatchAsync(
            client,
            HttpMethod.Delete,
            $"/statutes/{statute.Id}/articles/{article.Id}",
            null,
            refreshed!.Articles.Single().RowVersion);

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await fixture.AuditActionsAsync("statute_article", article.Id.ToString()))
            .ShouldBe(new[] { "created", "deleted" });
    }

    [Fact]
    public async Task The_list_is_paged_and_filterable_by_kind_and_name()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        await CreateStatuteAsync(client, "OtherStatute", "Ley de Listado Alfa");
        await CreateStatuteAsync(client, "OtherStatute", "Ley de Listado Beta");

        var others = await client.GetFromJsonAsync<StatuteListResponse>(
            new Uri("/statutes?kind=OtherStatute&page=1&pageSize=100", UriKind.Relative));

        others.ShouldNotBeNull();
        others!.Items.ShouldNotBeEmpty();
        others.Items.ShouldAllBe(item => item.Kind == "OtherStatute");
        others.Total.ShouldBe(await fixture.StatuteCountAsync("OtherStatute"));

        var searched = await client.GetFromJsonAsync<StatuteListResponse>(
            new Uri("/statutes?q=Listado%20Beta&page=1&pageSize=100", UriKind.Relative));

        searched.ShouldNotBeNull();
        searched!.Items.Select(item => item.Name).ShouldBe(new[] { "Ley de Listado Beta" });

        var pageSizeOne = await client.GetFromJsonAsync<StatuteListResponse>(
            new Uri("/statutes?kind=OtherStatute&page=1&pageSize=1", UriKind.Relative));

        pageSizeOne.ShouldNotBeNull();
        pageSizeOne!.Items.Count.ShouldBe(1);
        pageSizeOne.Total.ShouldBe(others.Total);

        var invalidKind = await client.GetAsync(new Uri("/statutes?kind=Promoted", UriKind.Relative));
        invalidKind.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(invalidKind)).Code.ShouldBe("validation_failed");
    }

    [Fact]
    public async Task The_comparison_returns_statute_articles_under_the_heading_but_never_for_an_excluded_one()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var salarios = await fixture.HeadingIdAsync("Salarios");
        var vacaciones = await fixture.HeadingIdAsync("Vacaciones");
        var excluded = await fixture.HeadingIdAsync("No Compara");

        var statute = await CreateStatuteAsync(client, "OtherStatute", "Ley Comparada");
        var classified = await AddArticleAsync(
            client,
            statute.Id,
            3,
            "Artículo 3 — Salarios",
            "El patrono pagará el salario en los primeros cinco días.",
            salarios);

        await AddArticleAsync(
            client,
            statute.Id,
            4,
            "Artículo 4 — Vacaciones",
            "Las vacaciones anuales serán de quince días hábiles.",
            vacaciones);

        await AddArticleAsync(
            client,
            statute.Id,
            5,
            "Artículo 5 — Sin clasificar",
            "Artículo sin clasificación de comparación.");

        var agreement = await CreateAgreementAsync(client, new DateOnly(2028, 1, 1));
        await AddClauseAsync(client, agreement.Id, 2, salarios);
        (await client.PostAsync(
                new Uri($"/agreements/{agreement.Id}/publish", UriKind.Relative), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await client.GetAsync(
            new Uri(
                $"/comparison?headingId={salarios}&agreementIds={agreement.Id}",
                UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var comparison = await response.Content.ReadFromJsonAsync<ComparisonView>();

        comparison.ShouldNotBeNull();
        comparison!.StatuteArticleTotal.ShouldBe(1);
        comparison.StatuteArticles.Count.ShouldBe(1);

        var article = comparison.StatuteArticles[0];
        article.StatuteId.ShouldBe(statute.Id);
        article.StatuteName.ShouldBe("Ley Comparada");
        article.StatuteKind.ShouldBe("OtherStatute");
        article.ArticleId.ShouldBe(classified.Id);
        article.Number.ShouldBe(3);
        article.Body.ShouldContain("El patrono pagará el salario");

        // Articles classified under another heading, and unclassified ones, are absent from the JSON.
        var json = await response.Content.ReadAsStringAsync();
        json.ShouldNotContain("Las vacaciones anuales");
        json.ShouldNotContain("Artículo sin clasificación");

        // An excluded heading produces no statute articles either: the exclusion is not a comparison.
        var excludedResponse = await client.GetAsync(
            new Uri(
                $"/comparison?headingId={excluded}&agreementIds={agreement.Id}",
                UriKind.Relative));

        excludedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var excludedComparison = await excludedResponse.Content.ReadFromJsonAsync<ComparisonView>();

        excludedComparison.ShouldNotBeNull();
        excludedComparison!.Heading.IsExcluded.ShouldBeTrue();
        excludedComparison.StatuteArticles.ShouldBeEmpty();
        excludedComparison.StatuteArticleTotal.ShouldBe(0);
    }

    [Fact]
    public async Task A_referenced_document_must_exist()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var before = await fixture.StatuteCountAsync();

        var response = await client.PostAsJsonAsync(
            new Uri("/statutes", UriKind.Relative),
            new CreateStatuteRequest(
                "OtherStatute", "Ley con Documento Inexistente", null, null, Guid.NewGuid()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response)).Code.ShouldBe("validation_failed");
        (await fixture.StatuteCountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_partial_update_keeps_omitted_fields_and_an_explicit_null_clears_a_nullable_one()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var statute = await CreateStatuteAsync(
            client,
            "OtherStatute",
            "Ley de Parcheo",
            new DateOnly(2014, 1, 30),
            "Gaceta Oficial 40.340");

        // Rename only. publishedOn and source are absent from the body, so they must survive — the
        // defect this test exists for silently cleared them.
        var renamedResponse = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}",
            PatchStatute(name: Str("Ley de Parcheo Renombrada")),
            statute.RowVersion);

        renamedResponse.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await renamedResponse.Content.ReadAsStringAsync());

        var renamed = await renamedResponse.Content.ReadFromJsonAsync<StatuteDetail>();

        renamed.ShouldNotBeNull();
        renamed!.Name.ShouldBe("Ley de Parcheo Renombrada");
        renamed.PublishedOn.ShouldBe(new DateOnly(2014, 1, 30));
        renamed.Source.ShouldBe("Gaceta Oficial 40.340");

        // The audit row must not claim the untouched fields changed.
        var changes = await fixture.AuditChangesAsync("statute", statute.Id.ToString());

        changes.Last().ShouldContain("Name");
        changes.Last().ShouldNotContain("PublishedOn");
        changes.Last().ShouldNotContain("Source");

        // An explicit null is how a nullable field is cleared.
        var clearedResponse = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}",
            PatchStatute(publishedOn: On(null), source: Str(null)),
            renamed.RowVersion);

        clearedResponse.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await clearedResponse.Content.ReadAsStringAsync());

        var cleared = await clearedResponse.Content.ReadFromJsonAsync<StatuteDetail>();

        cleared.ShouldNotBeNull();
        cleared!.PublishedOn.ShouldBeNull();
        cleared.Source.ShouldBeNull();
        cleared.Name.ShouldBe("Ley de Parcheo Renombrada");

        // A required field sent as null is refused rather than cleared.
        var refused = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}",
            PatchStatute(name: Str(null)),
            cleared.RowVersion);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(refused)).Code.ShouldBe("validation_failed");
        (await fixture.StatuteTextAsync(statute.Id, "name")).ShouldBe("Ley de Parcheo Renombrada");
    }

    [Fact]
    public async Task A_partial_article_update_changes_only_the_fields_it_sends()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var statute = await CreateStatuteAsync(client, "OtherStatute", "Ley de Artículo Parcial");
        var heading = await fixture.HeadingIdAsync("Salarios");
        var article = await AddArticleAsync(
            client,
            statute.Id,
            7,
            "Artículo 7 — Original",
            "Cuerpo original.",
            heading);

        var patchedResponse = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}/articles/{article.Id}",
            PatchArticle(title: Str("Artículo 7 — Título Nuevo")),
            article.RowVersion);

        patchedResponse.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await patchedResponse.Content.ReadAsStringAsync());

        var patched = await patchedResponse.Content.ReadFromJsonAsync<StatuteArticleResponse>();

        patched.ShouldNotBeNull();
        patched!.Number.ShouldBe(7);
        patched.Title.ShouldBe("Artículo 7 — Título Nuevo");
        patched.Body.ShouldBe("Cuerpo original.");
        patched.HeadingId.ShouldBe(heading);

        // Clearing the classification is an explicit null, and it leaves the rest alone.
        var clearedResponse = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}/articles/{article.Id}",
            PatchArticle(headingId: Id(null)),
            patched.RowVersion);

        clearedResponse.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await clearedResponse.Content.ReadAsStringAsync());

        var cleared = await clearedResponse.Content.ReadFromJsonAsync<StatuteArticleResponse>();

        cleared.ShouldNotBeNull();
        cleared!.HeadingId.ShouldBeNull();
        cleared.Title.ShouldBe("Artículo 7 — Título Nuevo");
        cleared.Body.ShouldBe("Cuerpo original.");

        // Required fields cannot be cleared, only omitted.
        var nullNumber = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}/articles/{article.Id}",
            PatchArticle(number: Num(null)),
            cleared.RowVersion);

        nullNumber.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(nullNumber)).Code.ShouldBe("validation_failed");

        var nullTitle = await SendWithIfMatchAsync(
            client,
            HttpMethod.Patch,
            $"/statutes/{statute.Id}/articles/{article.Id}",
            PatchArticle(title: Str(null)),
            cleared.RowVersion);

        nullTitle.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.ArticleCountAsync(statute.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Creating_a_statute_with_a_name_that_already_exists_is_refused()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var before = await fixture.StatuteCountAsync();
        await CreateStatuteAsync(client, "OtherStatute", "Ley de Nombre Único");

        var duplicate = await client.PostAsJsonAsync(
            new Uri("/statutes", UriKind.Relative),
            new CreateStatuteRequest("OtherStatute", "ley de nombre único", null, null, null));

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(duplicate);

        problem.Code.ShouldBe("conflict");
        problem.Detail.ShouldNotBeNull();
        problem.Detail!.ShouldContain(StatuteEndpoints.StatuteNameIndex);
        (await fixture.StatuteCountAsync()).ShouldBe(before + 1);
    }

    [Fact]
    public async Task The_comparison_bounds_statute_articles_at_100_and_reports_the_real_total()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var heading = await fixture.CreateHeadingAsync("Borde de Comparación");
        var statute = await CreateStatuteAsync(client, "OtherStatute", "Ley de Borde de Comparación");

        await fixture.InsertStatuteArticlesAsync(statute.Id, 1, 105, heading);

        var agreement = await CreateAgreementAsync(client, new DateOnly(2029, 2, 1));
        await AddClauseAsync(client, agreement.Id, 1, heading);
        (await client.PostAsync(
                new Uri($"/agreements/{agreement.Id}/publish", UriKind.Relative), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var comparison = await client.GetFromJsonAsync<ComparisonView>(
            new Uri($"/comparison?headingId={heading}&agreementIds={agreement.Id}", UriKind.Relative));

        comparison.ShouldNotBeNull();
        comparison!.StatuteArticles.Count.ShouldBe(StatutePaging.MaxComparisonArticles);
        comparison.StatuteArticleTotal.ShouldBe(105);

        // The array is the first N in the documented order, and the total says how many there are, so
        // a truncated answer is visible rather than silent.
        comparison.StatuteArticleTotal.ShouldBeGreaterThan(comparison.StatuteArticles.Count);
        comparison.StatuteArticles.Select(article => article.Number)
            .ShouldBe(Enumerable.Range(1, StatutePaging.MaxComparisonArticles));
        comparison.StatuteArticles.ShouldAllBe(article => article.StatuteId == statute.Id);
    }

    [Fact]
    public async Task The_comparison_requires_statute_read_because_it_returns_statutory_text()
    {
        using var editor = ClientFor(StatuteFixture.EditorA);
        var heading = await fixture.CreateHeadingAsync("Capacidad de Comparación");
        var statute = await CreateStatuteAsync(editor, "OtherStatute", "Ley de Capacidad");
        await AddArticleAsync(
            editor,
            statute.Id,
            1,
            "Artículo 1 — Capacidad",
            "El texto estatutario no debe filtrarse.",
            heading);

        var agreement = await CreateAgreementAsync(editor, new DateOnly(2029, 3, 1));
        await AddClauseAsync(editor, agreement.Id, 1, heading);
        (await editor.PostAsync(
                new Uri($"/agreements/{agreement.Id}/publish", UriKind.Relative), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        using var comparisonOnly = ClientFor(StatuteFixture.ComparisonOnly);

        var refused = await comparisonOnly.GetAsync(
            new Uri($"/comparison?headingId={heading}&agreementIds={agreement.Id}", UriKind.Relative));

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await ReadProblemAsync(refused);

        problem.Code.ShouldBe("forbidden");
        problem.Detail.ShouldNotBeNull();
        problem.Detail!.ShouldContain("statute.read");

        // The workspace itself is still theirs: the capability gates the statutory text, not the
        // screen.
        (await comparisonOnly.GetAsync(
                new Uri($"/comparison/candidates?headingId={heading}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // A caller holding both reads it.
        (await editor.GetAsync(
                new Uri($"/comparison?headingId={heading}&agreementIds={agreement.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_document_must_be_live_and_visible_to_the_caller()
    {
        using var editor = ClientFor(StatuteFixture.EditorA);

        var live = await fixture.CreateDocumentAsync(
            StatuteFixture.CompanyA, StatuteFixture.EditorA, "legal-live.pdf");

        var attachLive = await editor.PostAsJsonAsync(
            new Uri("/statutes", UriKind.Relative),
            new CreateStatuteRequest("OtherStatute", "Ley con Documento Vivo", null, null, live));

        attachLive.StatusCode.ShouldBe(HttpStatusCode.Created, await attachLive.Content.ReadAsStringAsync());

        var deleted = await fixture.CreateDocumentAsync(
            StatuteFixture.CompanyA, StatuteFixture.EditorA, "legal-deleted.pdf");
        await fixture.SoftDeleteDocumentAsync(deleted);

        var attachDeleted = await editor.PostAsJsonAsync(
            new Uri("/statutes", UriKind.Relative),
            new CreateStatuteRequest("OtherStatute", "Ley con Documento Borrado", null, null, deleted));

        attachDeleted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var deletedProblem = await ReadProblemAsync(attachDeleted);

        deletedProblem.Code.ShouldBe("validation_failed");

        // Another company's document is refused by the document policies, and the refusal is exactly
        // the one a missing id produces: the check cannot be used to probe for document ids.
        var foreign = await fixture.CreateDocumentAsync(
            StatuteFixture.CompanyB, StatuteFixture.EditorB, "legal-foreign.pdf");

        var attachForeign = await editor.PostAsJsonAsync(
            new Uri("/statutes", UriKind.Relative),
            new CreateStatuteRequest("OtherStatute", "Ley con Documento Ajeno", null, null, foreign));

        attachForeign.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var foreignProblem = await ReadProblemAsync(attachForeign);

        foreignProblem.Code.ShouldBe("validation_failed");
        foreignProblem.Detail.ShouldBe(deletedProblem.Detail);
    }

    [Fact]
    public async Task An_article_is_only_addressable_through_its_own_statute()
    {
        using var client = ClientFor(StatuteFixture.EditorA);
        var first = await CreateStatuteAsync(client, "OtherStatute", "Ley de Alcance Uno");
        var second = await CreateStatuteAsync(client, "OtherStatute", "Ley de Alcance Dos");
        var article = await AddArticleAsync(client, second.Id, 1, "Artículo 1 — De la segunda ley");

        (await client.GetAsync(
                new Uri($"/statutes/{first.Id}/articles/{article.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var source = await client.GetFromJsonAsync<StatuteArticleResponse>(
            new Uri($"/statutes/{second.Id}/articles/{article.Id}", UriKind.Relative));

        source.ShouldNotBeNull();
        source!.Title.ShouldBe("Artículo 1 — De la segunda ley");
    }

    [Fact]
    public async Task A_body_that_is_not_valid_json_for_the_contract_is_reported_as_a_problem()
    {
        using var client = ClientFor(StatuteFixture.EditorA);

        // `kind` is a string in the contract; a number cannot be bound to it.
        var response = await client.PostAsJsonAsync(
            new Uri("/statutes", UriKind.Relative),
            new { kind = 1, name = "Ley con Tipo Equivocado" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        var problem = await ReadProblemAsync(response);

        problem.Code.ShouldBe("malformed_request");
        problem.Detail.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_wrong_content_type_keeps_its_status_and_gains_a_problem_body()
    {
        using var client = ClientFor(StatuteFixture.EditorA);

        using var content = new StringContent("hello", Encoding.UTF8, "text/plain");
        var response = await client.PostAsync(new Uri("/statutes", UriKind.Relative), content);

        // The status is the framework's (415), unchanged; only the body is added, because the
        // contract names a code for this refusal and forbids a bare status.
        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        var problem = await ReadProblemAsync(response);

        problem.Code.ShouldBe("unsupported_media_type");
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

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<StatuteDetail> CreateStatuteAsync(
        HttpClient client,
        string kind,
        string name,
        DateOnly? publishedOn = null,
        string? source = null,
        Guid? documentId = null)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/statutes", UriKind.Relative),
            new CreateStatuteRequest(kind, name, publishedOn, source, documentId));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var statute = await response.Content.ReadFromJsonAsync<StatuteDetail>();
        statute.ShouldNotBeNull();
        return statute!;
    }

    private static async Task<StatuteArticleResponse> AddArticleAsync(
        HttpClient client,
        Guid statuteId,
        int number,
        string title,
        string body = "Texto del artículo.",
        Guid? headingId = null)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/statutes/{statuteId}/articles", UriKind.Relative),
            new StatuteArticleRequest(number, title, null, body, headingId, null));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var article = await response.Content.ReadFromJsonAsync<StatuteArticleResponse>();
        article.ShouldNotBeNull();
        return article!;
    }

    private static StatuteArticleRequest ArticleRequest(
        int number,
        string? title,
        Guid? headingId = null) =>
        new(number, title, null, "Texto del artículo.", headingId, null);

    // ---- Patch helpers ---------------------------------------------------------------------------
    //
    // A partial update distinguishes "not sent" (Unset) from "sent as null" (Set(null)). These
    // helpers make the body of each test say which of the two it means.

    private static PatchField<string?> Str(string? value) => PatchField<string?>.Set(value);

    private static PatchField<DateOnly?> On(DateOnly? value) => PatchField<DateOnly?>.Set(value);

    private static PatchField<Guid?> Id(Guid? value) => PatchField<Guid?>.Set(value);

    private static PatchField<int?> Num(int? value) => PatchField<int?>.Set(value);

    private static UpdateStatuteRequest PatchStatute(
        PatchField<string?> name = default,
        PatchField<DateOnly?> publishedOn = default,
        PatchField<string?> source = default,
        PatchField<Guid?> documentId = default) =>
        new(name, publishedOn, source, documentId);

    private static UpdateStatuteArticleRequest PatchArticle(
        PatchField<int?> number = default,
        PatchField<string?> title = default,
        PatchField<string?> summary = default,
        PatchField<string?> body = default,
        PatchField<Guid?> headingId = default,
        PatchField<string?> economicNote = default) =>
        new(number, title, summary, body, headingId, economicNote);

    private static async Task<AgreementDetail> CreateAgreementAsync(
        HttpClient client,
        DateOnly validFrom)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/agreements", UriKind.Relative),
            new CreateAgreementRequest(null, validFrom, null, "Nacional"));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var agreement = await response.Content.ReadFromJsonAsync<AgreementDetail>();
        agreement.ShouldNotBeNull();
        return agreement!;
    }

    private static async Task AddClauseAsync(
        HttpClient client,
        Guid agreementId,
        int number,
        Guid headingId)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/agreements/{agreementId}/clauses", UriKind.Relative),
            new ClauseRequest(
                number,
                $"Cláusula {number}",
                "Resumen",
                "Cuerpo de la cláusula del acuerdo.",
                headingId,
                null,
                false,
                null));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>Attempts the insert as <paramref name="userId"/> and always rolls back.</summary>
    private async Task<PostgresException?> TryInsertStatuteAsAsync(Guid userId, string name)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            await SetClaimsAndRoleAsync(connection, transaction, userId, "app_api");

            await using var insert = new NpgsqlCommand(
                "insert into app.statute (kind, name) values ('OtherStatute', @name)",
                connection,
                transaction);
            insert.Parameters.AddWithValue("name", name);
            await insert.ExecuteNonQueryAsync();

            return null;
        }
        catch (PostgresException exception)
        {
            return exception;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    /// <summary>Runs one scalar query as <paramref name="userId"/> inside a rolled-back transaction.</summary>
    private async Task<long> ScalarAsAsync(Guid userId, string sql)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            await SetClaimsAndRoleAsync(connection, transaction, userId, "app_api");

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    /// <summary>
    /// Puts the caller on the connection the way the API does: claims in
    /// <c>request.jwt.claims</c> and the API's database role.
    /// </summary>
    private static async Task SetClaimsAndRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        string role)
    {
        var claims = $$"""{"sub":"{{userId}}","role":"authenticated"}""";

        await using (var setClaims = new NpgsqlCommand(
            "select set_config('request.jwt.claims', @claims, true)", connection, transaction))
        {
            setClaims.Parameters.AddWithValue("claims", claims);
            await setClaims.ExecuteNonQueryAsync();
        }

        await using var setRole = new NpgsqlCommand($"set local role {role}", connection, transaction);
        await setRole.ExecuteNonQueryAsync();
    }

    private static async Task<HttpResponseMessage> SendWithIfMatchAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? content,
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

    private static async Task<(string? Code, string? Detail)> ReadProblemAsync(
        HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        return (
            root.TryGetProperty("code", out var code) ? code.GetString() : null,
            root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
    }
}
