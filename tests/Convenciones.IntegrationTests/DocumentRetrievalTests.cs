using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// The document retrieval contract: metadata, text, download and soft delete, all scoped to the
/// caller's company and all served from persisted state rather than from a claim.
/// </summary>
/// <remarks>
/// The authorization cases carry the weight. A list that happens to contain the right rows, or a text
/// endpoint that returns 404 for the wrong reason, proves little; every read here is checked against
/// what the database holds and against a caller from another company.
/// </remarks>
[Collection(DocumentsCollection.Name)]
public sealed class DocumentRetrievalTests(DocumentsFixture fixture)
{
    private static readonly Guid EditorA = new("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid EditorB = new("b1000000-0000-0000-0000-000000000002");
    private static readonly Guid Negotiator = new("b1000000-0000-0000-0000-000000000003");
    private static readonly Guid NoCompany = new("b1000000-0000-0000-0000-000000000004");
    private static readonly Guid NoRead = new("b1000000-0000-0000-0000-000000000005");
    private static readonly Guid Administrator = new("b1000000-0000-0000-0000-000000000006");

    private static readonly Guid CompanyA = new("b2000000-0000-0000-0000-000000000001");
    private static readonly Guid CompanyB = new("b2000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task List_returns_only_the_callers_company_and_the_real_total()
    {
        using var host = NewHost();
        using var editorA = ClientFor(host, EditorA);
        using var editorB = ClientFor(host, EditorB);

        var first = await UploadAsync(editorA, PdfSamples.TextPdf(), "listado-a-1.pdf");
        var second = await UploadAsync(editorA, PdfSamples.TextPdf(), "listado-a-2.pdf");
        var companyB = await UploadAsync(editorB, PdfSamples.TextPdf(), "listado-b.pdf");

        // The documents screen shows why a document failed without a request per row, so the list
        // projection carries the server-authored error.
        var failed = await UploadAsync(editorA, PdfSamples.BlankPdf(), "listado-fallido.pdf");
        (await ExtractAsync(editorA, failed.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var idsA = await ActiveDocumentIdsAsync(CompanyA);
        var idsB = await ActiveDocumentIdsAsync(CompanyB);

        var response = await editorA.GetAsync(new Uri("/documents?page=1&pageSize=100", UriKind.Relative));

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var page = await response.Content.ReadFromJsonAsync<DocumentListResponse>();
        page.ShouldNotBeNull();

        // The count is the number of visible rows, not an eyeballed subset.
        page!.Total.ShouldBe(idsA.Length);
        page.Items.Count.ShouldBeLessThanOrEqualTo(100);

        var listed = page.Items.Select(item => item.Id).ToArray();
        listed.ShouldAllBe(id => idsA.Contains(id));
        listed.ShouldContain(first.Id);
        listed.ShouldContain(second.Id);
        listed.ShouldNotContain(companyB.Id, "company B's document must not appear in company A's list");
        listed.ShouldNotContain(id => idsB.Contains(id));

        var failedSummary = page.Items.Single(item => item.Id == failed.Id);
        failedSummary.ExtractionStatus.ShouldBe("Failed");
        failedSummary.ExtractionError.ShouldNotBeNullOrWhiteSpace();

        var summary = page.Items.Single(item => item.Id == first.Id);
        summary.FileName.ShouldBe("listado-a-1.pdf");
        summary.ContentType.ShouldBe("application/pdf");
        summary.SizeBytes.ShouldBe(PdfSamples.TextPdf().Length);
        summary.ExtractionStatus.ShouldBe("NotRequested");
        summary.ExtractionAttempts.ShouldBe(0);
        summary.UploadedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));

        // Internal storage details must not be in the listing.
        var json = await response.Content.ReadAsStringAsync();
        var stored = await DocumentRowAsync(first.Id);
        json.ShouldNotContain("storage_path");
        json.ShouldNotContain("checksum");
        json.ShouldNotContain("uploaded_by");
        json.ShouldNotContain(stored.StoragePath);

        var fromB = await editorB.GetAsync(new Uri("/documents?page=1&pageSize=100", UriKind.Relative));
        fromB.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pageB = await fromB.Content.ReadFromJsonAsync<DocumentListResponse>();
        pageB.ShouldNotBeNull();
        pageB!.Total.ShouldBe(idsB.Length);
        pageB.Items.Select(item => item.Id).ShouldNotContain(first.Id);
    }

    [Fact]
    public async Task List_paginates_without_duplicating_or_skipping_the_new_documents()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var uploaded = new[]
        {
            await UploadAsync(client, PdfSamples.TextPdf(), "pagina-1.pdf"),
            await UploadAsync(client, PdfSamples.TextPdf(), "pagina-2.pdf"),
            await UploadAsync(client, PdfSamples.TextPdf(), "pagina-3.pdf"),
        };

        var firstPage = await ReadListAsync(client, "?page=1&pageSize=2");
        var secondPage = await ReadListAsync(client, "?page=2&pageSize=2");

        firstPage.Page.ShouldBe(1);
        firstPage.PageSize.ShouldBe(2);
        firstPage.Items.Count.ShouldBe(2);
        secondPage.Page.ShouldBe(2);
        secondPage.Total.ShouldBe(firstPage.Total);

        firstPage.Items.Select(item => item.Id)
            .Intersect(secondPage.Items.Select(item => item.Id))
            .ShouldBeEmpty("the same document must not appear on two pages");

        // The three newest documents span the first two pages, in any order.
        var firstTwo = firstPage.Items.Concat(secondPage.Items).Select(item => item.Id).ToArray();
        uploaded.Select(document => document.Id).ShouldAllBe(id => firstTwo.Contains(id));
    }

    [Fact]
    public async Task Metadata_returns_the_document_and_omits_internal_fields()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var uploaded = await UploadAsync(client, PdfSamples.TextPdf(), "metadatos.pdf");

        var response = await client.GetAsync(
            new Uri($"/documents/{uploaded.Id}", UriKind.Relative));

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var document = await response.Content.ReadFromJsonAsync<DocumentDetail>();
        document.ShouldNotBeNull();
        document!.Id.ShouldBe(uploaded.Id);
        document.FileName.ShouldBe("metadatos.pdf");
        document.ContentType.ShouldBe("application/pdf");
        document.SizeBytes.ShouldBe(PdfSamples.TextPdf().Length);
        document.ExtractionStatus.ShouldBe("NotRequested");
        document.ExtractionAttempts.ShouldBe(0);
        document.ExtractionError.ShouldBeNull();
        document.UploadedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        document.RowVersion.ShouldBeGreaterThan(0u, "rowVersion is required to delete the document");

        var stored = await DocumentRowAsync(uploaded.Id);
        stored.UploadedAt.ShouldBe(document.UploadedAt, TimeSpan.FromSeconds(1));

        var json = await response.Content.ReadAsStringAsync();
        json.ShouldNotContain("storage_path");
        json.ShouldNotContain("checksum");
        json.ShouldNotContain("uploaded_by");
        json.ShouldNotContain(stored.StoragePath);
        json.ShouldNotContain(stored.ChecksumSha256!);
    }

    [Fact]
    public async Task Text_returns_the_extracted_content_when_it_exists()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var uploaded = await UploadAsync(client, PdfSamples.TextPdf(), "texto.pdf");

        var extraction = await ExtractAsync(client, uploaded.Id);
        extraction.StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await client.GetAsync(
            new Uri($"/documents/{uploaded.Id}/text", UriKind.Relative));

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var text = await response.Content.ReadFromJsonAsync<DocumentTextResponse>();
        text.ShouldNotBeNull();
        text!.DocumentId.ShouldBe(uploaded.Id);
        text.Content.ShouldContain(PdfSamples.ExtractedText);
        text.CharCount.ShouldBe(text.Content.Length);
        text.ExtractedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));

        var stored = await StoredTextAsync(uploaded.Id);
        stored.ShouldNotBeNull();
        stored!.Content.ShouldBe(text.Content);
        stored.CharCount.ShouldBe(text.CharCount);

        var json = await response.Content.ReadAsStringAsync();
        json.ShouldNotContain("storage_path");
        json.ShouldNotContain("checksum");
        json.ShouldNotContain("uploadedBy");
        json.ShouldNotContain("uploaded_by");
    }

    [Fact]
    public async Task Text_returns_404_when_no_text_exists()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var notRequested = await UploadAsync(client, PdfSamples.TextPdf(), "sin-procesar.pdf");
        var noText = await UploadAsync(client, PdfSamples.BlankPdf(), "sin-texto.pdf");

        (await ExtractAsync(client, noText.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DocumentRowAsync(noText.Id)).Status.ShouldBe("Failed");

        foreach (var id in new[] { notRequested.Id, noText.Id })
        {
            var response = await client.GetAsync(
                new Uri($"/documents/{id}/text", UriKind.Relative));

            response.StatusCode.ShouldBe(
                HttpStatusCode.NotFound,
                "no text row must be 404, never an empty body or a 500");

            (await ReadCodeAsync(response)).ShouldBe("not_found");
        }
    }

    [Fact]
    public async Task Download_returns_the_stored_bytes_and_never_a_path()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var content = PdfSamples.TextPdf();
        var uploaded = await UploadAsync(client, content, "descarga.pdf");
        var stored = await DocumentRowAsync(uploaded.Id);

        var response = await client.GetAsync(
            new Uri($"/documents/{uploaded.Id}/download", UriKind.Relative));

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        response.Headers.Location.ShouldBeNull("the download is streamed, not redirected");
        response.Headers.CacheControl!.Private.ShouldBeTrue();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        var disposition = response.Content.Headers.ContentDisposition!.ToString();
        disposition.ShouldStartWith("attachment");
        disposition.ShouldContain("descarga.pdf");

        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(content);

        var headers = response.Headers.ToString() + response.Content.Headers;
        headers.ShouldNotContain(stored.StoragePath);
        headers.ShouldNotContain(stored.ChecksumSha256!);
        headers.ShouldNotContain("storage_path");

        // A range request proves the response is a stream, not a path handed to the client.
        using var rangeRequest = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"/documents/{uploaded.Id}/download", UriKind.Relative));
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 9);

        using var range = await client.SendAsync(rangeRequest);
        range.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        range.Content.Headers.ContentRange!.From.ShouldBe(0);
        (await range.Content.ReadAsByteArrayAsync()).ShouldBe(content[..10]);
    }

    [Fact]
    public async Task Every_endpoint_reports_another_companys_document_as_not_found()
    {
        using var host = NewHost();
        using var editorA = ClientFor(host, EditorA);
        using var editorB = ClientFor(host, EditorB);

        var uploaded = await UploadAsync(editorA, PdfSamples.TextPdf(), "ajeno.pdf");
        (await ExtractAsync(editorA, uploaded.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await editorB.GetAsync(new Uri($"/documents/{uploaded.Id}", UriKind.Relative));
        var text = await editorB.GetAsync(new Uri($"/documents/{uploaded.Id}/text", UriKind.Relative));
        var download = await editorB.GetAsync(
            new Uri($"/documents/{uploaded.Id}/download", UriKind.Relative));

        // No If-Match on purpose: the tenant check must run first, or the header error would leak
        // the fact that the document exists.
        var delete = await DeleteAsync(editorB, uploaded.Id, rowVersion: null);

        foreach (var response in new[] { detail, text, download, delete })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ReadCodeAsync(response)).ShouldBe("not_found");
        }

        // Company B's list must not mention it either.
        var page = await ReadListAsync(editorB, "?page=1&pageSize=100");
        page.Items.Select(item => item.Id).ShouldNotContain(uploaded.Id);

        // And the owner still has access: the refusals were about the caller, not the document.
        (await editorA.GetAsync(new Uri($"/documents/{uploaded.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_soft_deletes_removes_the_object_and_keeps_the_audit_trail()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var uploaded = await UploadAsync(client, PdfSamples.TextPdf(), "borrar.pdf");
        (await ExtractAsync(client, uploaded.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await ReadDetailAsync(client, uploaded.Id);
        var auditBefore = await AuditCountAsync(uploaded.Id);
        auditBefore.ShouldBeGreaterThan(0, "upload and extraction must already be audited");
        CountObjects(host).ShouldBe(1);

        var response = await DeleteAsync(client, uploaded.Id, detail.RowVersion);
        response.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            await response.Content.ReadAsStringAsync());

        var stored = await DocumentRowAsync(uploaded.Id);
        stored.DeletedAt.ShouldNotBeNull("soft delete keeps the row and records when it happened");
        CountObjects(host).ShouldBe(0, "the stored object is removed");

        // Every read now behaves as if the document never existed.
        (await client.GetAsync(new Uri($"/documents/{uploaded.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(new Uri($"/documents/{uploaded.Id}/text", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(new Uri($"/documents/{uploaded.Id}/download", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Extraction must not resurrect a deleted document either.
        (await ExtractAsync(client, uploaded.Id)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var page = await ReadListAsync(client, "?page=1&pageSize=100");
        page.Items.Select(item => item.Id).ShouldNotContain(uploaded.Id);
        page.Total.ShouldBe((await ActiveDocumentIdsAsync(CompanyA)).Length);

        // The audit trail survived, and the soft delete itself was recorded.
        var auditAfter = await AuditCountAsync(uploaded.Id);
        auditAfter.ShouldBeGreaterThan(auditBefore);

        var changes = await AuditChangesAsync(uploaded.Id);
        changes.ShouldContain(change => change.Contains("DeletedAt", StringComparison.Ordinal));

        var actors = await AuditActorsAsync(uploaded.Id);
        actors.ShouldContain(EditorA);

        // Already deleted: the second delete is a plain 404, not a second mutation.
        (await DeleteAsync(client, uploaded.Id, detail.RowVersion)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await AuditCountAsync(uploaded.Id)).ShouldBe(auditAfter);
    }

    [Fact]
    public async Task Delete_requires_the_current_row_version()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var uploaded = await UploadAsync(client, PdfSamples.TextPdf(), "version.pdf");
        var detail = await ReadDetailAsync(client, uploaded.Id);

        var missing = await DeleteAsync(client, uploaded.Id, rowVersion: null);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(missing)).ShouldBe("validation_failed");
        (await DocumentRowAsync(uploaded.Id)).DeletedAt.ShouldBeNull();

        var staleVersion = detail.RowVersion == 1 ? 2u : detail.RowVersion - 1;
        var stale = await DeleteAsync(client, uploaded.Id, staleVersion);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(stale)).ShouldBe("concurrent_modification");
        (await DocumentRowAsync(uploaded.Id)).DeletedAt.ShouldBeNull();
        CountObjects(host).ShouldBe(1);

        (await DeleteAsync(client, uploaded.Id, detail.RowVersion)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_without_document_write_is_forbidden_and_changes_nothing()
    {
        using var host = NewHost();
        using var editor = ClientFor(host, EditorA);
        using var negotiator = ClientFor(host, Negotiator);

        var uploaded = await UploadAsync(editor, PdfSamples.TextPdf(), "protegido.pdf");
        var detail = await ReadDetailAsync(editor, uploaded.Id);

        // The negotiator can read the document (document.read) but not delete it.
        (await negotiator.GetAsync(new Uri($"/documents/{uploaded.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await DeleteAsync(negotiator, uploaded.Id, detail.RowVersion);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe("forbidden");

        (await DocumentRowAsync(uploaded.Id)).DeletedAt.ShouldBeNull();
        CountObjects(host).ShouldBe(1);
    }

    [Fact]
    public async Task A_caller_without_a_company_sees_no_documents()
    {
        using var host = NewHost();
        using var editor = ClientFor(host, EditorA);
        using var noCompany = ClientFor(host, NoCompany);

        var uploaded = await UploadAsync(editor, PdfSamples.TextPdf(), "sin-empresa.pdf");

        var response = await noCompany.GetAsync(
            new Uri("/documents?page=1&pageSize=100", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<DocumentListResponse>();
        page.ShouldNotBeNull();
        page!.Total.ShouldBe(0, "no company means no documents, not every document");
        page.Items.ShouldBeEmpty();

        (await noCompany.GetAsync(new Uri($"/documents/{uploaded.Id}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cross_company_capability_lists_every_attributed_document()
    {
        using var host = NewHost();
        using var editorA = ClientFor(host, EditorA);
        using var editorB = ClientFor(host, EditorB);
        using var administrator = ClientFor(host, Administrator);

        var fromA = await UploadAsync(editorA, PdfSamples.TextPdf(), "admin-ve-a.pdf");
        var fromB = await UploadAsync(editorB, PdfSamples.TextPdf(), "admin-ve-b.pdf");

        var page = await ReadListAsync(administrator, "?page=1&pageSize=100");

        page.Items.Select(item => item.Id).ShouldContain(fromA.Id);
        page.Items.Select(item => item.Id).ShouldContain(fromB.Id);
        page.Total.ShouldBe(await AttributedDocumentCountAsync());
    }

    [Fact]
    public async Task Retrieve_without_document_read_is_forbidden()
    {
        using var host = NewHost();
        using var editor = ClientFor(host, EditorA);
        using var noRead = ClientFor(host, NoRead);

        var uploaded = await UploadAsync(editor, PdfSamples.TextPdf(), "sin-lectura.pdf");

        var requests = new[]
        {
            "/documents",
            $"/documents/{uploaded.Id}",
            $"/documents/{uploaded.Id}/text",
            $"/documents/{uploaded.Id}/download",
        };

        foreach (var path in requests)
        {
            var response = await noRead.GetAsync(new Uri(path, UriKind.Relative));

            response.StatusCode.ShouldBe(
                HttpStatusCode.Forbidden,
                $"{path} must require document.read");

            (await ReadCodeAsync(response)).ShouldBe("forbidden");
        }
    }

    private ApiFactory NewHost() => new(fixture.ConnectionString);

    private static HttpClient ClientFor(ApiFactory host, Guid subject)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(subject));
        return client;
    }

    private static async Task<DocumentResponse> UploadAsync(
        HttpClient client,
        byte[] content,
        string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);

        var response = await client.PostAsync(new Uri("/documents", UriKind.Relative), form);

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            $"the fixture PDF must be accepted before retrieval can be tested: "
            + await response.Content.ReadAsStringAsync());

        var document = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        document.ShouldNotBeNull();
        return document!;
    }

    private static Task<HttpResponseMessage> ExtractAsync(HttpClient client, Guid documentId) =>
        client.PostAsync(new Uri($"/documents/{documentId}/extract", UriKind.Relative), content: null);

    private static async Task<DocumentDetail> ReadDetailAsync(HttpClient client, Guid documentId)
    {
        var response = await client.GetAsync(new Uri($"/documents/{documentId}", UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var detail = await response.Content.ReadFromJsonAsync<DocumentDetail>();
        detail.ShouldNotBeNull();
        return detail!;
    }

    private static async Task<DocumentListResponse> ReadListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync(new Uri($"/documents{query}", UriKind.Relative));
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var page = await response.Content.ReadFromJsonAsync<DocumentListResponse>();
        page.ShouldNotBeNull();
        return page!;
    }

    private static async Task<HttpResponseMessage> DeleteAsync(
        HttpClient client,
        Guid documentId,
        uint? rowVersion)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/documents/{documentId}", UriKind.Relative));

        if (rowVersion is { } version)
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        }

        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem is not null && problem.TryGetValue("code", out var code)
            ? code?.ToString()
            : null;
    }

    private static int CountObjects(ApiFactory host) =>
        Directory.Exists(host.StorageRoot)
            ? Directory.EnumerateFiles(host.StorageRoot, "*", SearchOption.AllDirectories).Count()
            : 0;

    private async Task<int> AttributedDocumentCountAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select count(*) from platform.document
            where deleted_at is null and company_id is not null
            """, connection);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<Guid[]> ActiveDocumentIdsAsync(Guid companyId)    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select id from platform.document
            where company_id = @company and deleted_at is null
            """, connection);
        command.Parameters.AddWithValue("company", companyId);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids.ToArray();
    }

    private async Task<StoredDocument> DocumentRowAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select extraction_status::text, extraction_attempts, deleted_at, uploaded_at,
                   storage_path, checksum_sha256
            from platform.document
            where id = @id
            """, connection);
        command.Parameters.AddWithValue("id", documentId);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue($"document {documentId} must be persisted");

        return new StoredDocument(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private async Task<StoredText?> StoredTextAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "select content, char_count from platform.document_text where document_id = @id",
            connection);
        command.Parameters.AddWithValue("id", documentId);

        await using var reader = await command.ExecuteReaderAsync();

        return await reader.ReadAsync()
            ? new StoredText(reader.GetString(0), reader.GetInt32(1))
            : null;
    }

    private async Task<int> AuditCountAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select count(*) from platform.audit_entry
            where entity_type = 'document' and entity_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", documentId.ToString());

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<string[]> AuditChangesAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select coalesce(changes::text, '') from platform.audit_entry
            where entity_type = 'document' and entity_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", documentId.ToString());

        var changes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            changes.Add(reader.GetString(0));
        }

        return changes.ToArray();
    }

    private async Task<Guid[]> AuditActorsAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select distinct actor_id from platform.audit_entry
            where entity_type = 'document' and entity_id = @id and actor_id is not null
            """, connection);
        command.Parameters.AddWithValue("id", documentId.ToString());

        var actors = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            actors.Add(reader.GetGuid(0));
        }

        return actors.ToArray();
    }

    private sealed record StoredDocument(
        string Status,
        int Attempts,
        DateTimeOffset? DeletedAt,
        DateTimeOffset UploadedAt,
        string StoragePath,
        string? ChecksumSha256);

    private sealed record StoredText(string Content, int CharCount);
}
