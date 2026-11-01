using System.Net;
using System.Text;
using System.Text.Json;
using Convenciones.Ai;
using Convenciones.Ai.DocumentIntelligence;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Ai.DocumentIntelligence;

public sealed class AzureDocumentIntelligenceClientTests
{
    private static readonly string TestValue = string.Join("-", "test", "value");

    [Fact]
    public async Task Thirty_page_layout_maps_pages_paragraphs_and_offsets()
    {
        var layoutJson = BuildLayout(30);
        var handler = new StubHandler(
            Accepted(),
            Ok($$"""{"status":"succeeded","analyzeResult":{{layoutJson}}}"""));
        var client = CreateClient(handler);

        var layout = await client.AnalyzeAsync(new MemoryStream(new byte[] { 1, 2, 3 }));

        layout.PageCount.ShouldBe(30);
        layout.RequiresOcr.ShouldBeFalse();
        var first = layout.Pages[0];
        first.PageNumber.ShouldBe(1);
        first.Paragraphs.Count.ShouldBe(1);
        first.Paragraphs[0].Role.ShouldBe("sectionHeading");
        first.Paragraphs[0].BoundingBox.ShouldNotBeNull();
        first.NormalizedText.ShouldBe("Cláusula primera. Objeto del contrato colectivo de trabajo.");
        first.NormalizedToOriginalOffsets.Count.ShouldBe(first.NormalizedText.Length);
    }

    [Fact]
    public async Task Whitespace_is_collapsed_and_offsets_still_point_to_the_original()
    {
        var layoutJson = BuildLayout(1, "Cláusula\nObjeto");
        var handler = new StubHandler(
            Accepted(),
            Ok($$"""{"status":"succeeded","analyzeResult":{{layoutJson}}}"""));
        var client = CreateClient(handler);

        var layout = await client.AnalyzeAsync(new MemoryStream(new byte[] { 1 }));

        var page = layout.Pages[0];
        page.NormalizedText.ShouldBe("Cláusula Objeto");
        page.NormalizedToOriginalOffsets[8].ShouldBe(8);
        page.NormalizedToOriginalOffsets[9].ShouldBe(9);
    }

    [Fact]
    public async Task Scanned_pdf_is_reported_as_requiring_ocr()
    {
        var layoutJson = BuildLayout(1, string.Empty);
        var handler = new StubHandler(
            Accepted(),
            Ok($$"""{"status":"succeeded","analyzeResult":{{layoutJson}}}"""));
        var client = CreateClient(handler);

        var layout = await client.AnalyzeAsync(new MemoryStream(new byte[] { 1 }));

        layout.RequiresOcr.ShouldBeTrue();
        layout.CharactersPerPage.ShouldBe(0);
    }

    [Fact]
    public async Task Failed_operation_is_reported_as_invalid_response()
    {
        var handler = new StubHandler(
            Accepted(),
            Ok("""{"status":"failed"}"""));
        var client = CreateClient(handler);

        await Should.ThrowAsync<AiInvalidResponseException>(() => client.AnalyzeAsync(new MemoryStream(new byte[] { 1 })));
    }

    [Fact]
    public async Task Operation_that_never_finishes_times_out()
    {
        var handler = new StubHandler(
            Accepted(),
            Ok("""{"status":"running"}"""),
            Ok("""{"status":"running"}"""),
            Ok("""{"status":"running"}"""),
            Ok("""{"status":"running"}"""),
            Ok("""{"status":"running"}"""),
            Ok("""{"status":"running"}"""),
            Ok("""{"status":"running"}"""),
            Ok("""{"status":"running"}"""),
            Ok("""{"status":"running"}"""),
            Ok("""{"status":"running"}"""));
        var client = CreateClient(handler, timeoutSeconds: 1, pollMilliseconds: 300);

        await Should.ThrowAsync<AiTimeoutException>(() => client.AnalyzeAsync(new MemoryStream(new byte[] { 1 })));
    }

    [Fact]
    public async Task Missing_credential_fails_before_calling_the_service()
    {
        var handler = new StubHandler(Accepted());
        var options = new AzureDocumentIntelligenceOptions
        {
            Endpoint = new Uri("https://example.test/"),
            TimeoutSeconds = 5,
            PollIntervalMilliseconds = 10,
        };
        var client = new AzureDocumentIntelligenceClient(new HttpClient(handler), options);

        await Should.ThrowAsync<AiConfigurationException>(() => client.AnalyzeAsync(new MemoryStream(new byte[] { 1 })));
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Fake_reader_records_calls_and_returns_the_scripted_layout()
    {
        var fake = new FakeDocumentLayoutClient().Enqueue(new DocumentLayout(
            1,
            [],
            false,
            100,
            TimeSpan.FromMilliseconds(1)));

        var layout = await fake.AnalyzeAsync(new MemoryStream(new byte[] { 1 }));

        layout.PageCount.ShouldBe(1);
        fake.Calls.ShouldBe(1);
    }

    private static AzureDocumentIntelligenceClient CreateClient(
        StubHandler handler,
        int timeoutSeconds = 5,
        int pollMilliseconds = 10)
    {
        var options = new AzureDocumentIntelligenceOptions
        {
            Endpoint = new Uri("https://example.test/"),
            ApiKey = TestValue,
            TimeoutSeconds = timeoutSeconds,
            PollIntervalMilliseconds = pollMilliseconds,
            MinCharactersPerPage = 20,
        };

        return new AzureDocumentIntelligenceClient(new HttpClient(handler), options);
    }

    private static string BuildLayout(int pages, string text = "Cláusula primera. Objeto del contrato colectivo de trabajo.")
    {
        var pageElements = new List<object>();
        var paragraphs = new List<object>();
        var content = new StringBuilder();

        for (var number = 1; number <= pages; number++)
        {
            var offset = content.Length;
            content.Append(text).Append('\n');
            pageElements.Add(new
            {
                pageNumber = number,
                width = 612.0,
                height = 792.0,
                spans = new[] { new { offset, length = text.Length } },
                lines = new[] { new { content = text } },
            });

            paragraphs.Add(new
            {
                role = "sectionHeading",
                content = text,
                spans = new[] { new { offset, length = text.Length } },
                boundingRegions = new[]
                {
                    new { pageNumber = number, boundingBox = new[] { 1.0, 2.0, 3.0, 2.0, 3.0, 4.0, 1.0, 4.0 } },
                },
            });
        }

        return JsonSerializer.Serialize(new
        {
            content = content.ToString(),
            pages = pageElements,
            paragraphs,
        });
    }

    private static Func<CancellationToken, Task<HttpResponseMessage>> Accepted()
    {
        return _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted);
            response.Headers.TryAddWithoutValidation("Operation-Location", "https://example.test/operations/1");
            return Task.FromResult(response);
        };
    }

    private static Func<CancellationToken, Task<HttpResponseMessage>> Ok(string json)
    {
        return _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses;

        public List<HttpRequestMessage> Requests { get; } = [];

        public StubHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses)
        {
            _responses = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted HTTP response left.");
            }

            return _responses.Dequeue()(cancellationToken);
        }
    }
}
