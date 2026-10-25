using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Convenciones.Publishing;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// The public surface and news administration.
/// </summary>
/// <remarks>
/// The public tests run with **no** token: the point is that an anonymous caller reads exactly the
/// published content and nothing else. The projection tests enumerate the JSON property names rather than
/// spot-checking fields, because a field added to the public shape is a disclosure decision (T12 AC 2,
/// security-model AC-S8).
/// </remarks>
[Collection(PublishingCollection.Name)]
public sealed class PublicApiTests(PublishingFixture fixture) : IDisposable
{
    private readonly List<ApiFactory> _hosts = [];

    [Fact]
    public async Task Anonymous_reads_a_published_agreement_and_gets_404_for_a_draft()
    {
        var published = await fixture.InsertAgreementAsync(
            "Published", DateTimeOffset.UtcNow, PublishingFixture.Editor);
        await fixture.InsertClauseAsync(published, 1, "Salarios", "<p>Cláusula publicada</p>");
        var draft = await fixture.InsertAgreementAsync("Draft", null, null);
        await fixture.InsertClauseAsync(draft, 1, "Salarios", "<p>Borrador</p>");

        using var client = ClientFor(subject: null);

        var publishedResponse = await client.GetAsync(Path($"/public/agreements/{published}"));
        publishedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // AC-S7: a draft is a 404, indistinguishable from a missing agreement.
        var draftResponse = await client.GetAsync(Path($"/public/agreements/{draft}"));
        draftResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var problem = JsonDocument.Parse(await draftResponse.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("code").GetString().ShouldBe("not_found");

        var list = await client.GetFromJsonAsync<PublicAgreementListResponse>(
            Path("/public/agreements?pageSize=100"));

        list.ShouldNotBeNull();
        list!.Items.ShouldContain(item => item.Id == published);
        list.Items.ShouldNotContain(item => item.Id == draft);

        // The same answer from the database role itself: not one unpublished row is visible.
        var drafts = await fixture.CountAsAnonAsync(
            "select count(*) from app.agreement where state <> 'Published'");
        drafts.ShouldBe(0);
    }

    [Fact]
    public async Task The_public_agreement_projection_is_an_allow_list()
    {
        var agreementId = await fixture.InsertAgreementAsync(
            "Published", DateTimeOffset.UtcNow, PublishingFixture.Editor);
        await fixture.InsertClauseAsync(agreementId, 1, "Salarios", "<p>Primera</p>");
        await fixture.InsertAnnexAsync(agreementId, "Anexo I", "Descripción", "<p>Cuerpo del anexo</p>");

        using var client = ClientFor(subject: null);
        var response = await client.GetAsync(Path($"/public/agreements/{agreementId}"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Names(root).ShouldBe(
            ["annexes", "clauses", "companyName", "id", "publishedAt", "scope", "title", "validFrom", "validTo"],
            ignoreOrder: true);

        var clause = root.GetProperty("clauses")[0];
        Names(clause).ShouldBe(["body", "number", "summary", "title"], ignoreOrder: true);

        var annex = root.GetProperty("annexes")[0];
        Names(annex).ShouldBe(["body", "description", "name"], ignoreOrder: true);

        // Internal identifiers and identity columns never reach the public shape.
        var json = root.GetRawText();
        foreach (var forbidden in new[]
                 {
                     "companyId", "publishedBy", "createdBy", "updatedBy", "headingId", "documentId",
                     "storagePath", "state", "rowVersion",
                 })
        {
            json.ShouldNotContain(forbidden, Case.Sensitive);
        }

        var listResponse = await client.GetAsync(Path("/public/agreements?pageSize=100"));
        var listRoot = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()).RootElement;
        var item = listRoot.GetProperty("items").EnumerateArray()
            .Single(element => element.GetProperty("id").GetGuid() == agreementId);

        Names(item).ShouldBe(
            ["companyName", "id", "publishedAt", "scope", "title", "validFrom", "validTo"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task A_stored_script_tag_is_absent_from_the_public_agreement_response()
    {
        // Simulates a row written before the sanitiser existed: the public boundary sanitises again.
        var agreementId = await fixture.InsertAgreementAsync(
            "Published", DateTimeOffset.UtcNow, PublishingFixture.Editor);
        await fixture.InsertClauseAsync(
            agreementId,
            1,
            "Salarios",
            "<p>Hola</p><script>alert('clause')</script>");

        using var client = ClientFor(subject: null);
        var response = await client.GetAsync(Path($"/public/agreements/{agreementId}"));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("clauses")[0].GetProperty("body").GetString();

        body.ShouldNotBeNull();
        body!.ShouldNotContain("script", Case.Insensitive);
        body.ShouldContain("Hola");
    }

    [Fact]
    public async Task Anonymous_news_reads_are_limited_to_published_articles()
    {
        using var editor = ClientFor(PublishingFixture.Editor);
        var category = await CreateCategoryAsync(editor, "Portal T12 news");

        var draft = await CreateArticleAsync(editor, category.Id, "Borrador", "No debe salir");
        var published = await CreateArticleAsync(editor, category.Id, "Publicada", "Sí debe salir");
        (await PublishAsync(editor, published.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var client = ClientFor(subject: null);

        var list = await client.GetFromJsonAsync<PublicNewsListResponse>(Path("/public/news?pageSize=100"));

        list.ShouldNotBeNull();
        list!.Items.ShouldContain(item => item.Id == published.Id);
        list.Items.ShouldNotContain(item => item.Id == draft.Id);

        (await client.GetAsync(Path($"/public/news/{published.Id}"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(Path($"/public/news/{draft.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var draftRows = await fixture.CountAsAnonAsync(
            "select count(*) from app.news_article where state = 'Draft'");
        draftRows.ShouldBe(0);
    }

    [Fact]
    public async Task Unpublishing_removes_the_article_from_the_public_reads_immediately()
    {
        using var editor = ClientFor(PublishingFixture.Editor);
        var category = await CreateCategoryAsync(editor, "Portal T12 unpublish");
        var article = await CreateArticleAsync(editor, category.Id, "Efímera", "Cuerpo");

        (await PublishAsync(editor, article.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var anon = ClientFor(subject: null);

        (await anon.GetAsync(Path($"/public/news/{article.Id}"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await editor.PostAsync(Path($"/news/{article.Id}/unpublish"), null))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await anon.GetAsync(Path($"/public/news/{article.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var list = await anon.GetFromJsonAsync<PublicNewsListResponse>(Path("/public/news?pageSize=100"));
        list!.Items.ShouldNotContain(item => item.Id == article.Id);
    }

    [Fact]
    public async Task Publishing_requires_the_publish_capability()
    {
        using var editor = ClientFor(PublishingFixture.Editor);
        var category = await CreateCategoryAsync(editor, "Portal T12 capability");
        var article = await CreateArticleAsync(editor, category.Id, "Capacidad", "Cuerpo");

        using var writer = ClientFor(PublishingFixture.WriterWithoutPublish);

        var forbidden = await writer.PostAsync(Path($"/news/{article.Id}/publish"), null);

        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await fixture.NewsRowAsync(article.Id)).State.ShouldBe("Draft");

        // news.write is enough to create, which is what makes the separation meaningful.
        (await CreateArticleAsync(writer, category.Id, "Del escritor", "Cuerpo")).State.ShouldBe("Draft");
    }

    [Fact]
    public async Task The_database_records_the_publisher_and_the_publication_time()
    {
        using var editor = ClientFor(PublishingFixture.Editor);
        var category = await CreateCategoryAsync(editor, "Portal T12 publisher");
        var article = await CreateArticleAsync(editor, category.Id, "Autoría", "Cuerpo");

        var before = DateTimeOffset.UtcNow;
        (await PublishAsync(editor, article.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var row = await fixture.NewsRowAsync(article.Id);

        row.State.ShouldBe("Published");
        row.PublishedBy.ShouldBe(PublishingFixture.Editor);
        row.PublishedAt.ShouldNotBeNull();
        row.PublishedAt!.Value.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-5));

        // Editing the body afterwards does not rewrite the publication record.
        var patch = await editor.PatchAsJsonAsync(
            Path($"/news/{article.Id}"),
            new { body = "Cuerpo corregido" });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await fixture.NewsRowAsync(article.Id)).PublishedBy.ShouldBe(PublishingFixture.Editor);
    }

    [Fact]
    public async Task A_script_tag_in_a_news_body_is_absent_from_the_public_response()
    {
        using var editor = ClientFor(PublishingFixture.Editor);
        var category = await CreateCategoryAsync(editor, "Portal T12 sanitise");

        var article = await CreateArticleAsync(
            editor, category.Id, "Sanitizada", "<p>Hola</p><script>alert('news')</script>");

        var stored = await fixture.NewsRowAsync(article.Id);
        stored.Body.ShouldNotContain("script", Case.Insensitive);
        stored.Body.ShouldContain("Hola");

        (await PublishAsync(editor, article.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var anon = ClientFor(subject: null);
        var detail = await anon.GetFromJsonAsync<PublicNewsDetail>(Path($"/public/news/{article.Id}"));

        detail.ShouldNotBeNull();
        detail!.Body.ShouldNotContain("script", Case.Insensitive);
        detail.Body.ShouldContain("Hola");
    }

    [Fact]
    public async Task The_public_news_projection_is_an_allow_list()
    {
        using var editor = ClientFor(PublishingFixture.Editor);
        var category = await CreateCategoryAsync(editor, "Portal T12 shape");
        var article = await CreateArticleAsync(editor, category.Id, "Forma", "Cuerpo");
        (await PublishAsync(editor, article.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var anon = ClientFor(subject: null);
        var detail = await anon.GetAsync(Path($"/public/news/{article.Id}"));
        var detailRoot = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement;

        Names(detailRoot).ShouldBe(
            ["body", "categoryName", "id", "publishedAt", "summary", "title"],
            ignoreOrder: true);

        var list = await anon.GetAsync(Path("/public/news?pageSize=100"));
        var listRoot = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        var item = listRoot.GetProperty("items").EnumerateArray()
            .Single(element => element.GetProperty("id").GetGuid() == article.Id);

        Names(item).ShouldBe(
            ["categoryName", "id", "publishedAt", "summary", "title"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task News_administration_requires_a_capability_and_lists_drafts()
    {
        using var anon = ClientFor(subject: null);
        (await anon.GetAsync(Path("/news"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var editor = ClientFor(PublishingFixture.Editor);
        var category = await CreateCategoryAsync(editor, "Portal T12 admin");
        var article = await CreateArticleAsync(editor, category.Id, "Borrador admin", "Cuerpo");

        var drafts = await editor.GetFromJsonAsync<NewsListResponse>(Path("/news?state=Draft&pageSize=100"));

        drafts.ShouldNotBeNull();
        drafts!.Items.ShouldContain(item => item.Id == article.Id && item.State == "Draft");

        // The category name is unique case-insensitively at the database, and the API says 409.
        var duplicate = await CreateCategoryAsync(editor, "Portal T12 admin duplicate");
        var second = await editor.PostAsJsonAsync(
            Path("/news/categories"),
            new CreateNewsCategoryRequest("Portal T12 admin duplicate", null, 0));

        duplicate.Name.ShouldBe("Portal T12 admin duplicate");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private HttpClient ClientFor(Guid? subject)
    {
        var factory = new ApiFactory(fixture.ConnectionString);
        _hosts.Add(factory);
        var client = factory.CreateClient();

        if (subject is { } userId)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", TestAuth.Token(userId));
        }

        return client;
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }

    private static Uri Path(string path) => new(path, UriKind.Relative);

    private static string[] Names(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name).ToArray();

    private static async Task<NewsCategoryResponse> CreateCategoryAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(
            Path("/news/categories"),
            new CreateNewsCategoryRequest(name, "Categoría de prueba", 10));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var category = await response.Content.ReadFromJsonAsync<NewsCategoryResponse>();
        category.ShouldNotBeNull();
        return category!;
    }

    private static async Task<NewsDetail> CreateArticleAsync(
        HttpClient client,
        Guid categoryId,
        string title,
        string body)
    {
        var response = await client.PostAsJsonAsync(
            Path("/news"),
            new CreateNewsRequest(categoryId, title, "Resumen", body));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var article = await response.Content.ReadFromJsonAsync<NewsDetail>();
        article.ShouldNotBeNull();
        return article!;
    }

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Guid articleId) =>
        client.PostAsync(Path($"/news/{articleId}/publish"), null);
}
