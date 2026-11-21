using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Convenciones.Api.Endpoints;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-001 through the real pipeline: the quota charge and the run insert share one transaction, the
/// active-run cap holds across documents, and a repeated start returns the existing run without a
/// second charge.
/// </summary>
[Collection(DocumentsCollection.Name)]
public sealed class ProcessingStartTests(DocumentsFixture fixture)
{
    private static readonly Guid EditorA = new("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid CompanyA = new("b2000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Reading_processing_without_a_run_returns_run_not_found()
    {
        await ResetAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host);
        var document = await UploadAndExtractAsync(client);

        var noRun = await client.GetAsync(new Uri($"/documents/{document}/processing", UriKind.Relative));

        noRun.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using (var json = System.Text.Json.JsonDocument.Parse(await noRun.Content.ReadAsStringAsync()))
        {
            json.RootElement.GetProperty("code").GetString()
                .ShouldBe("run_not_found", "an accessible document without a run must be distinguishable from a foreign one");
        }

        var foreign = await client.GetAsync(new Uri($"/documents/{Guid.NewGuid()}/processing", UriKind.Relative));
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var foreignJson = System.Text.Json.JsonDocument.Parse(await foreign.Content.ReadAsStringAsync());
        foreignJson.RootElement.GetProperty("code").GetString()
            .ShouldBe("not_found", "a document that is not visible stays a plain not_found");
    }

    [Fact]
    public async Task Starting_a_run_charges_once_and_a_repeat_returns_the_same_run()
    {
        await ResetAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host);
        var document = await UploadAndExtractAsync(client);

        var first = await client.PostAsync(new Uri($"/documents/{document}/process", UriKind.Relative), null);
        first.StatusCode.ShouldBe(
            HttpStatusCode.Accepted,
            await first.Content.ReadAsStringAsync());

        var second = await client.PostAsync(new Uri($"/documents/{document}/process", UriKind.Relative), null);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted, await second.Content.ReadAsStringAsync());

        (await RunCountAsync(document)).ShouldBe(1);
        (await UsageDocumentsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_second_document_is_refused_by_the_active_run_cap_without_being_charged()
    {
        await ResetAsync();
        using var host = new ApiFactory(fixture.ConnectionString);
        using var client = ClientFor(host);

        var first = await UploadAndExtractAsync(client);
        var started = await client.PostAsync(new Uri($"/documents/{first}/process", UriKind.Relative), null);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());

        var second = await UploadAndExtractAsync(client);
        var blocked = await client.PostAsync(new Uri($"/documents/{second}/process", UriKind.Relative), null);

        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await blocked.Content.ReadAsStringAsync()).ShouldContain("concurrency_limit");
        (await RunCountAsync(second)).ShouldBe(0);
        (await UsageDocumentsAsync()).ShouldBe(1);
    }

    private static HttpClient ClientFor(ApiFactory host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(EditorA));
        return client;
    }

    private static async Task<Guid> UploadAndExtractAsync(HttpClient client)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(PdfSamples.TextPdf());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "contrato.pdf");

        var upload = await client.PostAsync(new Uri("/documents", UriKind.Relative), form);
        upload.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            $"the fixture PDF must upload before processing can be tested: {await upload.Content.ReadAsStringAsync()}");

        var document = await upload.Content.ReadFromJsonAsync<DocumentResponse>();
        document.ShouldNotBeNull();

        var extraction = await client.PostAsync(
            new Uri($"/documents/{document!.Id}/extract", UriKind.Relative), null);
        extraction.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"extraction must succeed before processing: {await extraction.Content.ReadAsStringAsync()}");

        return document.Id;
    }

    private async Task ResetAsync()
    {
        await ExecuteAsync("delete from platform.processing_run where company_id = @company;");
        await ExecuteAsync("delete from platform.processing_usage where company_id = @company;");
    }

    private async Task<int> RunCountAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from platform.processing_run where document_id = @document;", connection);
        command.Parameters.AddWithValue("document", documentId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> UsageDocumentsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select coalesce(sum(documents), 0) from platform.processing_usage where company_id = @company;",
            connection);
        command.Parameters.AddWithValue("company", CompanyA);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("company", CompanyA);
        await command.ExecuteNonQueryAsync();
    }
}
