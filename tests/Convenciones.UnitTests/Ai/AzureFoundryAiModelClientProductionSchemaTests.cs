using System.Net;
using System.Text;
using System.Text.Json;
using Convenciones.Ai;
using Convenciones.Worker.Extraction;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Ai;

public sealed class AzureFoundryAiModelClientProductionSchemaTests
{
    private static readonly string TestValue = string.Join("-", "test", "value");

    private const string CompleteClause =
        """{"clauses":[{"number":"1","heading":"Heading","text":"Clause text","page_from":1,"page_to":1,"confidence":0.9}]}""";

    private const string MinimalClause =
        """{"clauses":[{"text":"Clause text","page_from":1,"page_to":1,"confidence":0.5}]}""";

    [Fact]
    public async Task Complete_production_shape_is_accepted()
    {
        var client = Client(new StubHandler(Response(CompleteClause)));

        var result = await client.CompleteAsync(Request());

        result.Content.GetProperty("clauses").GetArrayLength().ShouldBe(1);
        result.Content.GetProperty("clauses")[0].GetProperty("number").GetString().ShouldBe("1");
        result.Content.GetProperty("clauses")[0].GetProperty("heading").GetString().ShouldBe("Heading");
        result.Content.GetProperty("clauses")[0].GetProperty("confidence").GetDouble().ShouldBe(0.9);
    }

    [Fact]
    public async Task Optional_number_and_heading_may_be_absent()
    {
        var client = Client(new StubHandler(Response(MinimalClause)));

        var result = await client.CompleteAsync(Request());

        result.Content.GetProperty("clauses")[0].TryGetProperty("number", out _).ShouldBeFalse();
        result.Content.GetProperty("clauses")[0].TryGetProperty("heading", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"clauses":[{"text":"Clause text","page_from":1,"confidence":0.5}]}""", "required")]
    [InlineData("""{"clauses":[{"text":"","page_from":1,"page_to":1,"confidence":0.5}]}""", "minLength")]
    [InlineData("""{"clauses":[{"text":"Clause text","page_from":0,"page_to":1,"confidence":0.5}]}""", "minimum")]
    [InlineData("""{"clauses":[{"text":"Clause text","page_from":1,"page_to":1,"confidence":1.1}]}""", "maximum")]
    [InlineData("""{"clauses":[{"text":"Clause text","page_from":1,"page_to":1,"confidence":0.5,"extra":true}]}""", "additionalProperties")]
    [InlineData("""{"clauses":[{"text":"Clause text","page_from":"one","page_to":1,"confidence":0.5}]}""", "type")]
    public async Task Production_schema_violations_report_the_failing_keyword(string invalid, string keyword)
    {
        var client = Client(new StubHandler(Response(invalid), Response(invalid)));

        var exception = await Should.ThrowAsync<AiInvalidResponseException>(() => client.CompleteAsync(Request()));

        exception.Message.ShouldContain("keyword=" + keyword);
        exception.Message.ShouldContain("shape=object");
    }

    [Fact]
    public async Task Complete_production_json_with_length_is_rejected()
    {
        var handler = new StubHandler(Response(CompleteClause, "length"));
        var client = Client(handler);

        var exception = await Should.ThrowAsync<AiInvalidResponseException>(() => client.CompleteAsync(Request()));

        exception.Message.ShouldContain("finish_reason_not_complete");
        exception.Message.ShouldContain("finishReason=length");
        handler.Bodies.Count.ShouldBe(1);
    }
    [Fact]
    public async Task Classification_production_shape_is_accepted()
    {
        const string valid =
            """{"classifications":[{"clause_ordinal":1,"categories":[{"key":"pricing","confidence":0.9}],"primary":"pricing","rationale":"Salario"}]}""";
        var client = Client(new StubHandler(Response(valid)));

        var result = await client.CompleteAsync(ClassificationRequest());

        result.Content.GetProperty("classifications").GetArrayLength().ShouldBe(1);
        result.Content.GetProperty("classifications")[0]
            .GetProperty("categories")[0]
            .GetProperty("key")
            .GetString()
            .ShouldBe("pricing");
    }

    [Fact]
    public async Task Truncated_classification_response_is_rejected_before_schema_validation()
    {
        var client = Client(new StubHandler(Response("""{"classifications":[""", "length")));

        var exception = await Should.ThrowAsync<AiInvalidResponseException>(
            () => client.CompleteAsync(ClassificationRequest()));

        exception.Message.ShouldContain("finish_reason_not_complete");
        exception.Message.ShouldContain("finishReason=length");
    }

    [Fact]
    public async Task Invalid_classification_shape_reports_the_failing_keyword()
    {
        const string invalid = """{"classifications":[{"clause_ordinal":1}]}""";
        var client = Client(new StubHandler(Response(invalid), Response(invalid)));

        var exception = await Should.ThrowAsync<AiInvalidResponseException>(
            () => client.CompleteAsync(ClassificationRequest()));

        exception.Message.ShouldContain("keyword=required");
    }

    [Fact]
    public async Task Repair_succeeds_with_the_structural_diagnostic()
    {
        const string invalid =
            """{"clauses":[{"text":"Clause text","page_from":"one","page_to":1,"confidence":0.5}]}""";
        var handler = new StubHandler(Response(invalid), Response(CompleteClause));
        var client = Client(handler);

        var result = await client.CompleteAsync(Request());

        result.Content.GetProperty("clauses").GetArrayLength().ShouldBe(1);
        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[1].ShouldContain("keyword=type");
        handler.Bodies[1].ShouldContain("finishReason=stop");
        handler.Bodies[1].ShouldNotContain("Clause text");
    }

    private static AiCompletionRequest Request() =>
        new("Eres un extractor de cláusulas.", "Extrae las cláusulas.", ExtractionSchema.Json);

    private static AiCompletionRequest ClassificationRequest() =>
        new("Clasifica las cláusulas.", "Clasifica este lote.", ClassificationSchema.Json);

    private static AzureFoundryAiModelClient Client(StubHandler handler) =>
        new(new HttpClient(handler), new AzureAiOptions
        {
            Endpoint = new Uri("https://example.test/"),
            Deployment = "clauses-mistral",
            ApiKey = TestValue,
            TimeoutSeconds = 5,
            MaxAttempts = 3,
            UseJsonSchema = false,
        });

    private static Func<CancellationToken, Task<HttpResponseMessage>> Response(string content, string? finishReason = "stop") =>
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
