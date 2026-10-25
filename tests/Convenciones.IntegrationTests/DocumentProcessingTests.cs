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
/// The extraction contract: real text persisted, "no text" and "damaged" reported as failures, retries
/// bounded, and no stale text surviving a failed attempt.
/// </summary>
/// <remarks>
/// These tests assert what the database holds and what the HTTP response says, not that a method was
/// called. The legacy defect this task exists to correct was a success reported for an operation that
/// produced nothing, so the failure cases assert the absence of a <c>document_text</c> row as carefully
/// as the success case asserts its content.
/// </remarks>
[Collection(DocumentsCollection.Name)]
public sealed class DocumentProcessingTests(DocumentsFixture fixture)
{
    private static readonly Guid EditorA = new("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid EditorB = new("b1000000-0000-0000-0000-000000000002");
    private static readonly Guid Negotiator = new("b1000000-0000-0000-0000-000000000003");

    [Fact]
    public async Task Successful_extraction_persists_the_pdf_text_and_matching_char_count()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var document = await UploadAsync(client, PdfSamples.TextPdf(), "contrato.pdf");

        var response = await ExtractAsync(client, document.Id);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"extraction is synchronous in the MVP and must answer 200: {await response.Content.ReadAsStringAsync()}");

        var extraction = await ReadExtractionAsync(response);
        extraction.Id.ShouldBe(document.Id);
        extraction.Status.ShouldBe("Completed");
        extraction.Attempts.ShouldBe(1);
        extraction.Error.ShouldBeNull();
        extraction.StartedAt.ShouldNotBeNull();
        extraction.CompletedAt.ShouldNotBeNull();
        extraction.CharCount.ShouldNotBeNull();

        var stored = await DocumentRowAsync(document.Id);
        stored.Status.ShouldBe("Completed");
        stored.Attempts.ShouldBe(1);
        stored.Error.ShouldBeNull();
        stored.StartedAt.ShouldNotBeNull();
        stored.CompletedAt.ShouldNotBeNull();

        var text = await TextRowAsync(document.Id);
        text.ShouldNotBeNull("a successful extraction must store the text");
        text!.Content.ShouldContain(PdfSamples.ExtractedText);

        // char_count is constrained by the database to equal length(content). Asserting all three
        // values means the round trip and the constraint agree with what the API reported.
        text.CharCount.ShouldBe(text.Content.Length);
        text.DatabaseLength.ShouldBe(text.Content.Length);
        text.CharCount.ShouldBe(text.DatabaseLength);
        extraction.CharCount.ShouldBe(text.CharCount);

        // The status endpoint must report the same final state after the request has finished.
        var status = await GetExtractionAsync(client, document.Id);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);

        var reported = await ReadExtractionAsync(status);
        reported.Status.ShouldBe("Completed");
        reported.CharCount.ShouldBe(text.CharCount);

        // A stored timestamp of year 1 would satisfy "not null" while telling an operator nothing.
        text.ExtractedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task A_blank_pdf_is_failed_with_a_reason_and_no_text_row()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var document = await UploadAsync(client, PdfSamples.BlankPdf(), "escaneado.pdf");

        var response = await ExtractAsync(client, document.Id);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var extraction = await ReadExtractionAsync(response);
        extraction.Status.ShouldBe("Failed", "a PDF with no text must never be reported as success");
        extraction.Attempts.ShouldBe(1);
        extraction.CharCount.ShouldBeNull();
        extraction.Error.ShouldNotBeNullOrWhiteSpace();
        extraction.Error.ShouldContain("text", Case.Insensitive);

        var stored = await DocumentRowAsync(document.Id);
        stored.Status.ShouldBe("Failed");
        stored.Attempts.ShouldBe(1);
        stored.Error.ShouldBe(extraction.Error);

        // The absence is the point: an empty row would let a reader claim the document had text.
        (await TextRowAsync(document.Id)).ShouldBeNull();

        var status = await GetExtractionAsync(client, document.Id);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadExtractionAsync(status)).CharCount.ShouldBeNull();
    }

    [Fact]
    public async Task Corrupt_stored_bytes_are_failed_and_the_reason_is_recorded()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        // A valid upload first, then the stored bytes are damaged. This is the "storage holds
        // something unreadable" case, which upload validation cannot prevent.
        var document = await UploadAsync(client, PdfSamples.TextPdf(), "dano.pdf");
        await ReplaceStoredBytesAsync(host, PdfSamples.TruncatedPdf());

        var response = await ExtractAsync(client, document.Id);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var extraction = await ReadExtractionAsync(response);
        extraction.Status.ShouldBe("Failed");
        extraction.Attempts.ShouldBe(1);
        extraction.CharCount.ShouldBeNull();
        extraction.Error.ShouldNotBeNullOrWhiteSpace();
        extraction.StartedAt.ShouldNotBeNull();
        extraction.CompletedAt.ShouldNotBeNull();

        var stored = await DocumentRowAsync(document.Id);
        stored.Status.ShouldBe("Failed");
        stored.Error.ShouldBe(extraction.Error);

        (await TextRowAsync(document.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task Re_extracting_a_completed_document_returns_409()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var document = await UploadAsync(client, PdfSamples.TextPdf(), "completo.pdf");

        (await ExtractAsync(client, document.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await ExtractAsync(client, document.Id);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(second)).ShouldBe("invalid_state");

        var stored = await DocumentRowAsync(document.Id);
        stored.Status.ShouldBe("Completed");
        stored.Attempts.ShouldBe(1, "a refused re-extraction must not count as an attempt");

        (await TextRowCountAsync(document.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Attempts_beyond_the_limit_are_rejected_with_409()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var document = await UploadAsync(client, PdfSamples.TextPdf(), "reintentos.pdf");
        await ReplaceStoredBytesAsync(host, PdfSamples.TruncatedPdf());

        for (var attempt = 1; attempt <= DocumentExtractionEndpoints.MaxExtractionAttempts; attempt++)
        {
            var response = await ExtractAsync(client, document.Id);

            response.StatusCode.ShouldBe(
                HttpStatusCode.OK,
                $"attempt {attempt} is within the limit: {await response.Content.ReadAsStringAsync()}");

            var extraction = await ReadExtractionAsync(response);
            extraction.Status.ShouldBe("Failed");
            extraction.Attempts.ShouldBe(attempt);
        }

        var refused = await ExtractAsync(client, document.Id);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(refused)).ShouldBe("invalid_state");

        var stored = await DocumentRowAsync(document.Id);
        stored.Status.ShouldBe("Failed");
        stored.Attempts.ShouldBe(DocumentExtractionEndpoints.MaxExtractionAttempts);
    }

    [Fact]
    public async Task Extraction_without_document_write_is_forbidden()
    {
        using var host = NewHost();
        using var uploader = ClientFor(host, EditorA);
        using var negotiator = ClientFor(host, Negotiator);

        // The document belongs to the negotiator's company, so only the capability can refuse this.
        var document = await UploadAsync(uploader, PdfSamples.TextPdf(), "permiso.pdf");

        var response = await ExtractAsync(negotiator, document.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe("forbidden");

        var stored = await DocumentRowAsync(document.Id);
        stored.Status.ShouldBe("NotRequested");
        stored.Attempts.ShouldBe(0, "a refused request must not consume an attempt");
        (await TextRowCountAsync(document.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task Another_companys_document_returns_404()
    {
        using var host = NewHost();
        using var editorA = ClientFor(host, EditorA);
        using var editorB = ClientFor(host, EditorB);

        var document = await UploadAsync(editorA, PdfSamples.TextPdf(), "ajeno.pdf");

        var extract = await ExtractAsync(editorB, document.Id);
        extract.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another company's document must not exist");
        (await ReadCodeAsync(extract)).ShouldBe("not_found");

        var status = await GetExtractionAsync(editorB, document.Id);
        status.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var stored = await DocumentRowAsync(document.Id);
        stored.Status.ShouldBe("NotRequested");
        stored.Attempts.ShouldBe(0);
        (await TextRowCountAsync(document.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_retry_removes_stale_extracted_text()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var document = await UploadAsync(client, PdfSamples.TextPdf(), "obsoleto.pdf");
        (await ExtractAsync(client, document.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TextRowCountAsync(document.Id)).ShouldBe(1);

        // A new attempt is requested while the document still has text from the previous success, and
        // the stored file is now damaged, so the new attempt must fail.
        await ReplaceStoredBytesAsync(host, PdfSamples.TruncatedPdf());
        await RequeueAsync(document.Id);

        var response = await ExtractAsync(client, document.Id);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var extraction = await ReadExtractionAsync(response);
        extraction.Status.ShouldBe("Failed");
        extraction.Attempts.ShouldBe(2);
        extraction.CharCount.ShouldBeNull();

        // The stale row is gone, so obsolete content cannot be served by a later read.
        (await TextRowAsync(document.Id)).ShouldBeNull();
        (await TextRowCountAsync(document.Id)).ShouldBe(0);

        var status = await GetExtractionAsync(client, document.Id);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadExtractionAsync(status)).CharCount.ShouldBeNull();
    }

    [Fact]
    public async Task A_missing_stored_file_is_failed_and_removes_stale_text()
    {
        using var host = NewHost();
        using var client = ClientFor(host, EditorA);

        var document = await UploadAsync(client, PdfSamples.TextPdf(), "perdido.pdf");
        (await ExtractAsync(client, document.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TextRowCountAsync(document.Id)).ShouldBe(1);

        // The object disappeared from storage. That is a failed attempt like any other and must not
        // leave the text of the previous success readable.
        File.Delete(StoredPath(host));
        await RequeueAsync(document.Id);

        var response = await ExtractAsync(client, document.Id);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var extraction = await ReadExtractionAsync(response);
        extraction.Status.ShouldBe("Failed");
        extraction.Attempts.ShouldBe(2);
        extraction.Error.ShouldNotBeNullOrWhiteSpace();
        extraction.Error.ShouldContain("could not be found", Case.Insensitive);
        extraction.CharCount.ShouldBeNull();

        (await TextRowAsync(document.Id)).ShouldBeNull();
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
            $"the fixture PDF must be accepted before extraction can be tested: "
            + await response.Content.ReadAsStringAsync());

        var document = await response.Content.ReadFromJsonAsync<DocumentResponse>();
        document.ShouldNotBeNull();
        return document!;
    }

    private static Task<HttpResponseMessage> ExtractAsync(HttpClient client, Guid documentId) =>
        client.PostAsync(new Uri($"/documents/{documentId}/extract", UriKind.Relative), content: null);

    private static Task<HttpResponseMessage> GetExtractionAsync(HttpClient client, Guid documentId) =>
        client.GetAsync(new Uri($"/documents/{documentId}/extraction", UriKind.Relative));

    private static async Task<ExtractionResponse> ReadExtractionAsync(HttpResponseMessage response)
    {
        var extraction = await response.Content.ReadFromJsonAsync<ExtractionResponse>();
        extraction.ShouldNotBeNull();
        return extraction!;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem is not null && problem.TryGetValue("code", out var code)
            ? code?.ToString()
            : null;
    }

    /// <summary>Overwrites the bytes the storage adapter holds, behind the API's back.</summary>
    private static Task ReplaceStoredBytesAsync(ApiFactory host, byte[] content) =>
        File.WriteAllBytesAsync(StoredPath(host), content);

    /// <summary>The one object the storage adapter holds for this test's host.</summary>
    private static string StoredPath(ApiFactory host) =>
        Directory.EnumerateFiles(host.StorageRoot, "*.pdf", SearchOption.AllDirectories).Single();

    /// <summary>
    /// Puts a document back into the requestable state while keeping its previous text, which is the
    /// state a failed retry has to clean up.
    /// </summary>
    private async Task RequeueAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            update platform.document
            set extraction_status = 'Requested', extraction_error = null
            where id = @id
            """, connection);
        command.Parameters.AddWithValue("id", documentId);

        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    private async Task<StoredDocument> DocumentRowAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select extraction_status::text, extraction_attempts, extraction_error,
                   extraction_started_at, extraction_completed_at
            from platform.document
            where id = @id
            """, connection);
        command.Parameters.AddWithValue("id", documentId);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue($"document {documentId} must be persisted");

        return new StoredDocument(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }

    private async Task<StoredText?> TextRowAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            select content, char_count, length(content), extracted_at
            from platform.document_text
            where document_id = @id
            """, connection);
        command.Parameters.AddWithValue("id", documentId);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new StoredText(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetFieldValue<DateTimeOffset>(3));
    }

    private async Task<int> TextRowCountAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "select count(*) from platform.document_text where document_id = @id", connection);
        command.Parameters.AddWithValue("id", documentId);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed record StoredDocument(
        string Status,
        int Attempts,
        string? Error,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt);

    private sealed record StoredText(
        string Content,
        int CharCount,
        int DatabaseLength,
        DateTimeOffset ExtractedAt);
}
