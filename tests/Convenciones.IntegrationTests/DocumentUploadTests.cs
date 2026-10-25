using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Convenciones.Api.Endpoints;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// The upload contract: PDF only, 20 MiB, private storage, nothing left behind on failure.
/// </summary>
/// <remarks>
/// The rejection cases carry the weight. Accepting a valid PDF proves the happy path works; only the
/// rejections prove the rule exists. Every failure case also counts the storage objects and the database
/// rows afterwards, because "rejected" and "rejected without a side effect" are different claims.
/// </remarks>
[Collection(DocumentsCollection.Name)]
public sealed class DocumentUploadTests(DocumentsFixture fixture)
{
    private static readonly Guid EditorA = new("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid EditorB = new("b1000000-0000-0000-0000-000000000002");
    private static readonly Guid Negotiator = new("b1000000-0000-0000-0000-000000000003");

    private static readonly Guid CompanyA = new("b2000000-0000-0000-0000-000000000001");
    private static readonly Guid CompanyB = new("b2000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Valid_pdf_is_accepted_and_stored_byte_for_byte()
    {
        var content = PdfSamples.ValidPdf();
        var (client, factory) = Client(EditorA);

        try
        {
            var response = await UploadAsync(client, content, "contrato.pdf", "application/pdf");

            response.StatusCode.ShouldBe(
                HttpStatusCode.Created,
                $"expected 201, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

            var document = await response.Content.ReadFromJsonAsync<DocumentResponse>();
            document.ShouldNotBeNull();
            document!.ContentType.ShouldBe("application/pdf");
            document.SizeBytes.ShouldBe(content.Length);
            document.ExtractionStatus.ShouldBe("NotRequested", "uploading is not processing");
            document.ChecksumSha256.ShouldBe(Sha256(content));

            // The stored bytes must be identical, not merely present.
            var stored = Directory
                .EnumerateFiles(factory.StorageRoot, "*.pdf", SearchOption.AllDirectories)
                .Single();
            (await File.ReadAllBytesAsync(stored)).ShouldBe(content);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Pdf_of_exactly_twenty_mib_is_accepted()
    {
        var content = PdfSamples.ValidPdfOfSize(DocumentEndpoints.MaxDocumentBytes);
        content.Length.ShouldBe(20_971_520);

        var (client, _) = Client(EditorA);
        try
        {
            var response = await UploadAsync(client, content, "limite.pdf", "application/pdf");

            response.StatusCode.ShouldBe(
                HttpStatusCode.Created,
                $"expected 201 at the boundary, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task One_byte_over_the_limit_is_rejected_with_413()
    {
        var content = PdfSamples.ValidPdfOfSize(DocumentEndpoints.MaxDocumentBytes + 1);
        content.Length.ShouldBe(20_971_521);

        var (client, factory) = Client(EditorA);
        try
        {
            var objectsBefore = CountObjects(factory);
            var rowsBefore = await fixture.DocumentRowCountAsync();

            var response = await UploadAsync(client, content, "demasiado.pdf", "application/pdf");

            response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
            CountObjects(factory).ShouldBe(objectsBefore, "an oversized upload must not be stored");
            (await fixture.DocumentRowCountAsync()).ShouldBe(rowsBefore);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Theory]
    [InlineData("contrato.pdf", "application/pdf")]
    [InlineData("foto.jpg", "image/jpeg")]
    public async Task Jpeg_is_rejected_even_when_it_claims_to_be_a_pdf(string fileName, string contentType)
    {
        var (client, factory) = Client(EditorA);
        try
        {
            var objectsBefore = CountObjects(factory);
            var rowsBefore = await fixture.DocumentRowCountAsync();

            var response = await UploadAsync(client, PdfSamples.Jpeg(), fileName, contentType);

            response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
            CountObjects(factory).ShouldBe(objectsBefore);
            (await fixture.DocumentRowCountAsync()).ShouldBe(rowsBefore);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Theory]
    [InlineData("imagen.png")]
    [InlineData("documento.doc")]
    [InlineData("documento.docx")]
    [InlineData("notas.txt")]
    [InlineData("archivo.zip")]
    public async Task Non_pdf_formats_are_rejected_with_415(string fileName)
    {
        var content = fileName switch
        {
            "imagen.png" => PdfSamples.Png(),
            "documento.doc" => PdfSamples.OleDocument(),
            "documento.docx" or "archivo.zip" => PdfSamples.ZipContainer(),
            _ => PdfSamples.PlainText(),
        };

        var (client, factory) = Client(EditorA);
        try
        {
            var objectsBefore = CountObjects(factory);
            var rowsBefore = await fixture.DocumentRowCountAsync();

            var response = await UploadAsync(client, content, fileName, "application/pdf");

            response.StatusCode.ShouldBe(
                HttpStatusCode.UnsupportedMediaType,
                $"{fileName} must be rejected regardless of its declared content type");
            CountObjects(factory).ShouldBe(objectsBefore);
            (await fixture.DocumentRowCountAsync()).ShouldBe(rowsBefore);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Text_renamed_to_pdf_is_rejected()
    {
        var (client, factory) = Client(EditorA);
        try
        {
            var objectsBefore = CountObjects(factory);
            var response = await UploadAsync(client, PdfSamples.PlainText(), "contrato.pdf", "application/pdf");

            response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
            CountObjects(factory).ShouldBe(objectsBefore);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Empty_file_is_rejected_with_400()
    {
        var (client, _) = Client(EditorA);
        try
        {
            var response = await UploadAsync(client, [], "vacio.pdf", "application/pdf");

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Truncated_pdf_is_rejected_with_400()
    {
        var (client, factory) = Client(EditorA);
        try
        {
            var objectsBefore = CountObjects(factory);

            var response = await UploadAsync(
                client, PdfSamples.TruncatedPdf(), "roto.pdf", "application/pdf");

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReadCodeAsync(response)).ShouldBe("validation_failed");
            CountObjects(factory).ShouldBe(objectsBefore);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_pdf_header_alone_is_not_evidence()
    {
        var content = PdfSamples.HeaderOnlyPdf();

        // The point of the test: the signature check would pass and the parser must refuse.
        content.Take(5).ShouldBe("%PDF-"u8.ToArray());

        var (client, factory) = Client(EditorA);
        try
        {
            var objectsBefore = CountObjects(factory);
            var response = await UploadAsync(client, content, "falso.pdf", "application/pdf");

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            CountObjects(factory).ShouldBe(objectsBefore);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Upload_without_document_write_is_forbidden_and_stores_nothing()
    {
        var (client, factory) = Client(Negotiator);
        try
        {
            var objectsBefore = CountObjects(factory);

            var response = await UploadAsync(
                client, PdfSamples.ValidPdf(), "contrato.pdf", "application/pdf");

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            CountObjects(factory).ShouldBe(objectsBefore);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Client_supplied_company_id_is_ignored()
    {
        var (client, _) = Client(EditorA);
        try
        {
            var content = PdfSamples.ValidPdf();
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(file, "file", "contrato.pdf");

            // The client claims another tenant. The server must take the company from the session.
            form.Add(new StringContent(CompanyB.ToString()), "company_id");

            var response = await client.PostAsync(new Uri("/documents", UriKind.Relative), form);

            response.StatusCode.ShouldBe(
                HttpStatusCode.Created,
                await response.Content.ReadAsStringAsync());

            var document = await response.Content.ReadFromJsonAsync<DocumentResponse>();
            document.ShouldNotBeNull();

            var storedCompany = await fixture.QueryCompanyAsync(document!.Id);
            storedCompany.ShouldBe(CompanyA, "the client-supplied company id must be ignored");
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Each_company_uploads_into_its_own_company()
    {
        var (clientA, _) = Client(EditorA);
        var (clientB, _) = Client(EditorB);

        try
        {
            var first = await UploadAsync(clientA, PdfSamples.ValidPdf(), "a.pdf", "application/pdf");
            var second = await UploadAsync(clientB, PdfSamples.ValidPdf(), "b.pdf", "application/pdf");

            first.StatusCode.ShouldBe(HttpStatusCode.Created);
            second.StatusCode.ShouldBe(HttpStatusCode.Created);

            var documentA = await first.Content.ReadFromJsonAsync<DocumentResponse>();
            var documentB = await second.Content.ReadFromJsonAsync<DocumentResponse>();

            (await fixture.QueryCompanyAsync(documentA!.Id)).ShouldBe(CompanyA);
            (await fixture.QueryCompanyAsync(documentB!.Id)).ShouldBe(CompanyB);
        }
        finally
        {
            clientA.Dispose();
            clientB.Dispose();
        }
    }

    private (HttpClient Client, ApiFactory Factory) Client(Guid subject)
    {
        // Each call gets its own storage root so object counts are unambiguous.
        var factory = new ApiFactory(fixture.ConnectionString, NewRoot());
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(subject));
        return (client, factory);
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), $"convenciones-docs-{Guid.NewGuid():N}");

    private static int CountObjects(ApiFactory factory) =>
        Directory.Exists(factory.StorageRoot)
            ? Directory.EnumerateFiles(factory.StorageRoot, "*", SearchOption.AllDirectories).Count()
            : 0;

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        byte[] content,
        string fileName,
        string contentType)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        return await client.PostAsync(new Uri("/documents", UriKind.Relative), form);
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem is not null && problem.TryGetValue("code", out var code)
            ? code?.ToString()
            : null;
    }
}
