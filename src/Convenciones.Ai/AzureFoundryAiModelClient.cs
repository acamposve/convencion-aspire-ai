using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Convenciones.Ai;

/// <summary>
/// Azure AI Foundry chat-completions adapter with schema validation and one repair attempt.
/// </summary>
/// <remarks>
/// The model is asked for JSON; the answer is parsed and validated against the schema carried by the
/// request. One repair attempt appends the validation error, and a second failure is reported as
/// <see cref="AiInvalidResponseException"/> so the caller never persists unvalidated content.
/// Transient provider failures (429/5xx) are retried with exponential backoff, bounded by
/// <see cref="AzureAiOptions.MaxAttempts"/>.
/// </remarks>
public sealed class AzureFoundryAiModelClient : IAiModelClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly AzureAiOptions _options;

    public AzureFoundryAiModelClient(HttpClient http, AzureAiOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<AiCompletionResult> CompleteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _options.Validate();

        var stopwatch = Stopwatch.StartNew();
        var repaired = false;
        string? repairReason = null;
        string? firstDiagnostic = null;
        var transientAttempt = 0;

        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            HttpResponseMessage response;
            try
            {
                using var message = BuildMessage(request, repairReason);
                response = await _http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiTimeoutException(
                    $"The model did not answer within {_options.TimeoutSeconds} seconds.");
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    transientAttempt++;
                    if (transientAttempt >= _options.MaxAttempts)
                    {
                        throw new AiUnavailableException(
                            $"The model provider returned {(int)response.StatusCode} after {transientAttempt} attempts.");
                    }

                    await Task.Delay(Backoff(transientAttempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new AiInvalidResponseException(
                        $"The model provider rejected the request with status {(int)response.StatusCode}.");
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var (content, model, finishReason) = ReadContent(payload);
                var validation = MatchesSchema(content, request.JsonSchema);
                var diagnostic = DescribeDiagnostic(validation.Error, content.Length, finishReason);

                if (!validation.Valid)
                {
                    if (!repaired)
                    {
                        repaired = true;
                        firstDiagnostic = diagnostic;
                        repairReason = diagnostic;
                        continue;
                    }

                    throw new AiInvalidResponseException(
                        "The model did not return JSON that satisfies the requested schema after one repair attempt. " +
                        "first[" + firstDiagnostic + "] repair[" + diagnostic + "]");
                }

                using var contentDocument = JsonDocument.Parse(content);
                var (inputTokens, outputTokens) = ReadUsage(payload);

                return new AiCompletionResult(
                    contentDocument.RootElement.Clone(),
                    model ?? _options.Deployment,
                    request.Deployment ?? _options.Deployment,
                    inputTokens,
                    outputTokens,
                    stopwatch.Elapsed);
            }
        }
    }

    private HttpRequestMessage BuildMessage(AiCompletionRequest request, string? repairReason)
    {
        var messages = new List<object>
        {
            new { role = "system", content = request.SystemPrompt },
            new { role = "user", content = request.UserPrompt },
        };

        if (repairReason is not null)
        {
            messages.Add(new
            {
                role = "user",
                content =
                    "La respuesta anterior no cumplió el esquema solicitado: " + repairReason +
                    ". Devuelve únicamente el JSON válido, sin texto adicional.",
            });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Deployment ?? _options.Deployment,
            ["messages"] = messages,
            ["temperature"] = _options.Temperature,
        };

        if (request.MaxOutputTokens is { } max)
        {
            body["max_tokens"] = max;
        }

        if (_options.UseJsonSchema)
        {
            body["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "response",
                    strict = true,
                    schema = JsonNode.Parse(request.JsonSchema),
                },
            };
        }
        else
        {
            body["response_format"] = new { type = "json_object" };
        }

        var uri = BuildUri();
        var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, SerializerOptions),
                Encoding.UTF8,
                "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            message.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);
        }
        else
        {
            message.Headers.Authorization = new("Bearer", _options.AccessToken);
        }

        return message;
    }

    private Uri BuildUri()
    {
        var relative = _options.ChatCompletionsPath.TrimStart('/');
        var builder = new UriBuilder(new Uri(_options.Endpoint!, relative))
        {
            Query = "api-version=" + Uri.EscapeDataString(_options.ApiVersion),
        };
        return builder.Uri;
    }

    private static (string Content, string? Model, string? FinishReason) ReadContent(string payload)
    {
        using var envelope = JsonDocument.Parse(payload);
        var root = envelope.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new AiInvalidResponseException("The provider response has no choices.");
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var contentElement)
            || contentElement.GetString() is not { Length: > 0 } content)
        {
            throw new AiInvalidResponseException("The provider response has no message content.");
        }

        var model = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
        var finishReason = choice.TryGetProperty("finish_reason", out var finishElement)
            && finishElement.ValueKind is JsonValueKind.String
            ? finishElement.GetString()
            : null;
        return (content, model, finishReason);
    }

    private static (int? Input, int? Output) ReadUsage(string payload)
    {
        using var envelope = JsonDocument.Parse(payload);
        if (!envelope.RootElement.TryGetProperty("usage", out var usage))
        {
            return (null, null);
        }

        var input = usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var p) ? p : (int?)null;
        var output = usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var c) ? c : (int?)null;
        return (input, output);
    }

    private static (bool Valid, string Error) MatchesSchema(string json, string schemaText)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return (false, $"invalid JSON at line {exception.LineNumber}, position {exception.BytePositionInLine}");
        }

        using (document)
        {
            JsonSchema schema;
            try
            {
                schema = JsonSchema.FromText(schemaText);
            }
            catch (JsonException exception)
            {
                throw new AiConfigurationException("The JSON schema is not valid: " + exception.Message);
            }

            var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            return result.IsValid
                ? (true, string.Empty)
                : (false, DescribeValidation(result) + "; shape=" + DescribeShape(document.RootElement));
        }
    }

    private static readonly HashSet<string> KnownFields =
    [
        "clauses", "text", "number", "heading", "page_from", "page_to", "confidence"
    ];

    private static string SafeFieldName(string name) =>
        KnownFields.Contains(name) ? name : "<unexpected>";

    private static string SafeLocation(string location)
    {
        var segments = location.Split('/');

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0 || KnownFields.Contains(segment))
            {
                continue;
            }

            // Only an array index directly after the clauses array is useful and safe to retain.
            if (i > 0 && segments[i - 1] == "clauses" && segment.All(char.IsDigit))
            {
                continue;
            }

            segments[i] = "<unexpected>";
        }

        return string.Join("/", segments);
    }

    private static string DescribeDiagnostic(string error, int contentLength, string? finishReason) =>
        $"contentLength={contentLength};finishReason={finishReason ?? "unknown"};error={error}";

    private static string DescribeValidation(EvaluationResults result, int depth = 0)
    {
        if (depth >= 4)
        {
            return "validation-error-depth-limit";
        }

        var parts = new List<string>();
        if (result.Errors is not null)
        {
            foreach (var error in result.Errors)
            {
                parts.Add($"{SafeLocation(result.InstanceLocation.ToString())} {result.EvaluationPath} keyword={error.Key}");
            }
        }

        if (result.Details is not null)
        {
            foreach (var detail in result.Details)
            {
                if (!detail.IsValid)
                {
                    parts.Add(DescribeValidation(detail, depth + 1));
                }
            }
        }

        return parts.Count == 0 ? "the JSON does not match the requested schema" : string.Join("; ", parts.Take(8));
    }
    private static string DescribeShape(JsonElement element, int depth = 0)
    {
        if (depth >= 3)
        {
            return element.ValueKind.ToString();
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => "object{" + string.Join(",", element.EnumerateObject()
                .Select(property => SafeFieldName(property.Name) + ":" + DescribeShape(property.Value, depth + 1))) + "}",
            JsonValueKind.Array => "array[" + element.GetArrayLength() + "]" +
                (element.GetArrayLength() > 0 ? ":" + DescribeShape(element[0], depth + 1) : string.Empty),
            _ => element.ValueKind.ToString(),
        };
    }
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(250 * Math.Pow(2, Math.Max(0, attempt - 1)));
}
