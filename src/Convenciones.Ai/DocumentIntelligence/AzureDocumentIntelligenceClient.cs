using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Convenciones.Ai.DocumentIntelligence;

/// <summary>
/// Azure AI Document Intelligence <c>prebuilt-layout</c> client (v4.0, long-running operation).
/// </summary>
/// <remarks>
/// The PDF is sent once, the operation is polled with a deadline, and the answer is mapped to pages,
/// paragraphs, spans and boxes. A PDF whose pages carry almost no text is returned with
/// <see cref="DocumentLayout.RequiresOcr"/> instead of being reported as an empty success.
/// </remarks>
public sealed class AzureDocumentIntelligenceClient : IDocumentLayoutClient
{
    private readonly HttpClient _http;
    private readonly AzureDocumentIntelligenceOptions _options;
    private readonly IAccessTokenProvider? _tokenProvider;

    public AzureDocumentIntelligenceClient(HttpClient http, AzureDocumentIntelligenceOptions options)
        : this(http, options, null)
    {
    }

    public AzureDocumentIntelligenceClient(
        HttpClient http,
        AzureDocumentIntelligenceOptions options,
        IAccessTokenProvider? tokenProvider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenProvider = tokenProvider;
    }

    public async Task<DocumentLayout> AnalyzeAsync(Stream pdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        _options.Validate();

        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            var (operation, inlinePayload) = await StartAsync(pdf, deadline.Token).ConfigureAwait(false);
            var payload = inlinePayload ?? await PollAsync(operation!, deadline.Token).ConfigureAwait(false);
            var layout = Parse(payload, stopwatch.Elapsed);
            return layout;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiTimeoutException(
                $"Document Intelligence did not finish within {_options.TimeoutSeconds} seconds.");
        }
    }

    private async Task<(string? Operation, string? Payload)> StartAsync(
        Stream pdf,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildAnalyzeUri());
        request.Content = new StreamContent(pdf);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        await AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            if (response.Headers.TryGetValues("Operation-Location", out var values))
            {
                var operation = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(operation))
                {
                    return (operation, null);
                }
            }

            throw new AiInvalidResponseException("Document Intelligence accepted the PDF without an operation location.");
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            // Some deployments answer synchronously; the body already contains analyzeResult.
            return (null, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        throw Failure(response.StatusCode);
    }

    private async Task<string> PollAsync(string operation, CancellationToken cancellationToken)
    {
        if (operation.Length == 0)
        {
            return string.Empty;
        }

        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, operation);
            await AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response.StatusCode);
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            var status = document.RootElement.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return payload;
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new AiInvalidResponseException("Document Intelligence reported the analysis as failed.");
            }

            await Task.Delay(_options.PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private static AiException Failure(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new AiConfigurationException($"Document Intelligence rejected the credential ({ (int)statusCode }).")
            : statusCode is HttpStatusCode.TooManyRequests || (int)statusCode >= 500
                ? new AiUnavailableException($"Document Intelligence is unavailable ({ (int)statusCode }).")
                : new AiInvalidResponseException($"Document Intelligence rejected the PDF ({ (int)statusCode }).");
    }

    private async Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", _options.ApiKey);
            return;
        }

        if (_tokenProvider is null)
        {
            throw new AiConfigurationException("Document Intelligence requires an access token provider.");
        }

        var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private Uri BuildAnalyzeUri()
    {
        var relative = $"documentintelligence/documentModels/{Uri.EscapeDataString(_options.ModelId)}:analyze";
        var builder = new UriBuilder(new Uri(_options.Endpoint!, relative))
        {
            Query = "api-version=" + Uri.EscapeDataString(_options.ApiVersion),
        };
        return builder.Uri;
    }

    private DocumentLayout Parse(string payload, TimeSpan latency)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (!root.TryGetProperty("analyzeResult", out var analyzeResult))
        {
            throw new AiInvalidResponseException("Document Intelligence returned no analyzeResult.");
        }

        var content = analyzeResult.TryGetProperty("content", out var contentElement)
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;

        var paragraphs = ReadParagraphs(analyzeResult);
        var pages = new List<LayoutPage>();
        var totalCharacters = 0;

        if (analyzeResult.TryGetProperty("pages", out var pagesElement))
        {
            foreach (var pageElement in pagesElement.EnumerateArray())
            {
                var page = ReadPage(pageElement, content, paragraphs);
                pages.Add(page);
                totalCharacters += page.Text.Count(character => !char.IsWhiteSpace(character));
            }
        }

        if (pages.Count == 0)
        {
            throw new AiInvalidResponseException("Document Intelligence returned no pages.");
        }

        var charactersPerPage = totalCharacters / pages.Count;
        return new DocumentLayout(
            pages.Count,
            pages,
            charactersPerPage < _options.MinCharactersPerPage,
            charactersPerPage,
            latency);
    }

    private static LayoutPage ReadPage(
        JsonElement pageElement,
        string content,
        IReadOnlyList<LayoutParagraph> allParagraphs)
    {
        var pageNumber = pageElement.GetProperty("pageNumber").GetInt32();
        var width = pageElement.TryGetProperty("width", out var widthElement) ? widthElement.GetDouble() : 0;
        var height = pageElement.TryGetProperty("height", out var heightElement) ? heightElement.GetDouble() : 0;

        var spans = ReadSpans(pageElement);
        var text = ReadPageText(pageElement, content, spans);
        var normalized = TextNormalization.Normalize(text);
        var paragraphs = allParagraphs.Where(paragraph => paragraph.PageNumber == pageNumber).ToArray();

        return new LayoutPage(
            pageNumber,
            width,
            height,
            text,
            normalized.Text,
            normalized.OriginalOffsets,
            paragraphs,
            spans);
    }

    private static string ReadPageText(JsonElement pageElement, string content, IReadOnlyList<LayoutSpan> spans)
    {
        if (pageElement.TryGetProperty("lines", out var lines))
        {
            var values = lines
                .EnumerateArray()
                .Select(line => line.TryGetProperty("content", out var lineContent) ? lineContent.GetString() : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (values.Length > 0)
            {
                return string.Join("\n", values);
            }
        }

        if (content.Length > 0 && spans.Count > 0)
        {
            var pieces = new List<string>(spans.Count);
            foreach (var span in spans)
            {
                if (span.Offset >= 0 && span.Length > 0 && span.Offset + span.Length <= content.Length)
                {
                    pieces.Add(content.Substring(span.Offset, span.Length));
                }
            }

            if (pieces.Count > 0)
            {
                return string.Join("\n", pieces);
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<LayoutParagraph> ReadParagraphs(JsonElement analyzeResult)
    {
        if (!analyzeResult.TryGetProperty("paragraphs", out var paragraphs))
        {
            return [];
        }

        var result = new List<LayoutParagraph>();
        foreach (var paragraph in paragraphs.EnumerateArray())
        {
            var content = paragraph.TryGetProperty("content", out var contentElement)
                ? contentElement.GetString() ?? string.Empty
                : string.Empty;
            var role = paragraph.TryGetProperty("role", out var roleElement) ? roleElement.GetString() : null;
            var spans = ReadSpans(paragraph);

            if (!paragraph.TryGetProperty("boundingRegions", out var regions) || regions.GetArrayLength() == 0)
            {
                continue;
            }

            foreach (var region in regions.EnumerateArray())
            {
                var pageNumber = region.TryGetProperty("pageNumber", out var pageElement) ? pageElement.GetInt32() : 0;
                var box = ReadBoundingBox(region);
                result.Add(new LayoutParagraph(role, content, pageNumber, box, spans));
            }
        }

        return result;
    }

    private static IReadOnlyList<LayoutSpan> ReadSpans(JsonElement element)
    {
        if (!element.TryGetProperty("spans", out var spans))
        {
            return [];
        }

        return spans
            .EnumerateArray()
            .Select(span => new LayoutSpan(
                span.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0,
                span.TryGetProperty("length", out var length) ? length.GetInt32() : 0))
            .ToArray();
    }

    private static LayoutBoundingBox? ReadBoundingBox(JsonElement region)
    {
        if (!region.TryGetProperty("boundingBox", out var box) || box.GetArrayLength() < 8)
        {
            return null;
        }

        var values = box.EnumerateArray().Select(value => value.GetDouble()).ToArray();
        var xs = new[] { values[0], values[2], values[4], values[6] };
        var ys = new[] { values[1], values[3], values[5], values[7] };
        return new LayoutBoundingBox(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min());
    }
}
