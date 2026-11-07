using System.Net;
using System.Text;
using System.Text.Json;
using Convenciones.Ai;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Ai;

public sealed class AzureFoundryAiModelClientDiagnosticsTests
{
    private static readonly string TestValue = string.Join("-", "test", "value");

    private const string Schema = """
        {
          "type": "object",
          "required": ["clauses"],
          "additionalProperties": false,
          "properties": {
            "clauses": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["text", "page_from"],
                "additionalProperties": false,
                "properties": {
                  "text": { "type": "string" },
                  "page_from": { "type": "integer" }
                }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task Invalid_schema_reports_paths_shape_and_finish_reason_without_content()
    {
        const string contractText = "CONFIDENTIAL-CONTRACT-TEXT";
        var invalid = """{"clauses":[{"text":"CONFIDENTIAL-CONTRACT-TEXT","page_from":"one","SECRET_FIELD":"CONFIDENTIAL-CONTRACT-TEXT","123456789":"CONFIDENTIAL-CONTRACT-TEXT"}]}""";
        var handler = new StubHandler(Response(invalid, "stop"), Response(invalid, "stop"));
        var client = Client(handler, useJsonSchema: false);

        var exception = await Should.ThrowAsync<AiInvalidResponseException>(() => client.CompleteAsync(Request()));

        exception.Message.ShouldContain("contentLength=");
        exception.Message.ShouldContain("finishReason=stop");
        exception.Message.ShouldContain("keyword=type");
        exception.Message.ShouldContain("shape=object");
        exception.Message.ShouldNotContain(contractText);
        exception.Message.ShouldNotContain("SECRET_FIELD");
        exception.Message.ShouldNotContain("123456789");
        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[1].ShouldContain("keyword=type");
    }

    [Fact]
    public async Task Length_finish_reason_is_rejected_even_with_schema_valid_json()
    {
        var handler = new StubHandler(Response("""{"clauses":[]}""", "length"));
        var client = Client(handler, useJsonSchema: false);

        var exception = await Should.ThrowAsync<AiInvalidResponseException>(() => client.CompleteAsync(Request()));

        exception.Message.ShouldContain("finish_reason_not_complete");
        exception.Message.ShouldContain("finishReason=length");
        handler.Bodies.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("content_filter")]
    [InlineData("tool_calls")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Non_stop_finish_reasons_are_rejected(string? finishReason)
    {
        var handler = new StubHandler(Response("""{"clauses":[]}""", finishReason));
        var client = Client(handler, useJsonSchema: false);

        var exception = await Should.ThrowAsync<AiInvalidResponseException>(() => client.CompleteAsync(Request()));

        exception.Message.ShouldContain("finish_reason_not_complete");
        handler.Bodies.Count.ShouldBe(1);
    }
    [Fact]
    public async Task Malformed_json_reports_position_and_never_echoes_the_body()
    {
        const string contractText = "CONFIDENTIAL-CONTRACT-TEXT";
        var malformed = "```json\n" + contractText + "\n```";
        var handler = new StubHandler(Response(malformed, "stop"), Response(malformed, "stop"));
        var client = Client(handler, useJsonSchema: false);

        var exception = await Should.ThrowAsync<AiInvalidResponseException>(() => client.CompleteAsync(Request()));

        exception.Message.ShouldContain("invalid JSON");
        exception.Message.ShouldContain("finishReason=stop");
        exception.Message.ShouldNotContain(contractText);
        exception.Message.ShouldNotContain("SECRET_FIELD");
        exception.Message.ShouldNotContain("123456789");
    }

    [Fact]
    public async Task Missing_required_field_reports_required_keyword()
    {
        var invalid = """{"clauses":[{"text":"clause"}]}""";
        var handler = new StubHandler(Response(invalid, "stop"), Response(invalid, "stop"));
        var client = Client(handler, useJsonSchema: false);

        var exception = await Should.ThrowAsync<AiInvalidResponseException>(() => client.CompleteAsync(Request()));

        exception.Message.ShouldContain("keyword=required");
    }

    [Fact]
    public async Task Truncated_json_reports_length_without_content()
    {
        const string fragment = "CONFIDENTIAL-CONTRACT-TEXT";
        var handler = new StubHandler(
            Response("""{"clauses":[{"text":"CONFIDENTIAL-CONTRACT-TEXT",""", "length"),
            Response("""{"clauses":[{"text":"CONFIDENTIAL-CONTRACT-TEXT",""", "length"));
        var client = Client(handler, useJsonSchema: false);

        var exception = await Should.ThrowAsync<AiInvalidResponseException>(() => client.CompleteAsync(Request()));

        exception.Message.ShouldContain("finishReason=length");
        exception.Message.ShouldContain("finish_reason_not_complete");
        exception.Message.ShouldNotContain(fragment);
    }

    [Fact]
    public async Task Empty_content_is_rejected_without_calling_the_model_twice()
    {
        var handler = new StubHandler(Response(string.Empty, "stop"));
        var client = Client(handler, useJsonSchema: false);

        await Should.ThrowAsync<AiInvalidResponseException>(() => client.CompleteAsync(Request()));
        handler.Bodies.Count.ShouldBe(1);
    }
    [Fact]
    public async Task UseJsonSchema_sends_structured_output_contract()
    {
        var handler = new StubHandler(Response("""{"clauses":[]}""", "stop"));
        var client = Client(handler, useJsonSchema: true);

        await client.CompleteAsync(Request());

        handler.Bodies.Count.ShouldBe(1);
        handler.Bodies[0].ShouldContain("\"type\":\"json_schema\"");
        handler.Bodies[0].ShouldContain("\"strict\":true");
        handler.Bodies[0].ShouldNotContain("\"type\":\"json_object\"");
    }

    private static AiCompletionRequest Request() =>
        new("Eres un extractor de cláusulas.", "Extrae las cláusulas.", Schema);

    private static AzureFoundryAiModelClient Client(StubHandler handler, bool useJsonSchema)
    {
        var options = new AzureAiOptions
        {
            Endpoint = new Uri("https://example.test/"),
            Deployment = "clauses-mistral",
            ApiKey = TestValue,
            TimeoutSeconds = 5,
            MaxAttempts = 3,
            UseJsonSchema = useJsonSchema,
        };
        return new AzureFoundryAiModelClient(new HttpClient(handler), options);
    }

    private static Func<CancellationToken, Task<HttpResponseMessage>> Response(string content, string? finishReason) =>
        _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content }, finish_reason = finishReason } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5 },
                    model = "mistral-large-3",
                }),
                Encoding.UTF8,
                "application/json"),
        });

    private sealed class StubHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new(responses);

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return await _responses.Dequeue()(cancellationToken);
        }
    }
}
