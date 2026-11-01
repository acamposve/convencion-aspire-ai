using System.Net;
using System.Text;
using System.Text.Json;
using Convenciones.Ai;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Ai;

public sealed class AzureFoundryAiModelClientTests
{
    private static readonly string TestValue = string.Join("-", "test", "value");

    private const string Schema =
        """{"type":"object","required":["clauses"],"properties":{"clauses":{"type":"array"}}}""";

    private static AiCompletionRequest Request() =>
        new("Eres un extractor de cláusulas.", "Extrae las cláusulas del contrato.", Schema);

    [Fact]
    public async Task Valid_answer_is_returned_with_tokens_and_model()
    {
        var handler = new StubHandler(JsonResponse("""{"clauses":[]}""", input: 42, output: 7));
        var client = CreateClient(handler);

        var result = await client.CompleteAsync(Request());

        result.Content.GetProperty("clauses").GetArrayLength().ShouldBe(0);
        result.InputTokens.ShouldBe(42);
        result.OutputTokens.ShouldBe(7);
        result.Model.ShouldBe("mistral-large-3");
        result.Deployment.ShouldBe("clauses");
        handler.Bodies.Count.ShouldBe(1);
        handler.Bodies[0].ShouldContain("\"model\":\"clauses\"");
        handler.Bodies[0].ShouldContain("\"response_format\":{\"type\":\"json_object\"}");
    }

    [Fact]
    public async Task Invalid_answer_is_repaired_once()
    {
        var handler = new StubHandler(
            JsonResponse("not-json", input: 10, output: 2),
            JsonResponse("""{"clauses":[]}""", input: 20, output: 3));
        var client = CreateClient(handler);

        var result = await client.CompleteAsync(Request());

        result.Content.GetProperty("clauses").GetArrayLength().ShouldBe(0);
        handler.Bodies.Count.ShouldBe(2);
        using var repair = JsonDocument.Parse(handler.Bodies[1]);
        var repairMessage = repair.RootElement.GetProperty("messages")[2].GetProperty("content").GetString();
        repairMessage.ShouldNotBeNull();
        repairMessage.ShouldContain("no cumplió el esquema");
    }

    [Fact]
    public async Task Second_invalid_answer_fails_without_persisting()
    {
        var handler = new StubHandler(
            JsonResponse("""{"wrong":true}"""),
            JsonResponse("""{"still":"wrong"}"""));
        var client = CreateClient(handler);

        await Should.ThrowAsync<AiInvalidResponseException>(() => client.CompleteAsync(Request()));
        handler.Bodies.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Transient_429_is_retried()
    {
        var handler = new StubHandler(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)),
            JsonResponse("""{"clauses":[]}"""));
        var client = CreateClient(handler);

        var result = await client.CompleteAsync(Request());

        handler.Bodies.Count.ShouldBe(2);
        result.Content.GetProperty("clauses").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Timeout_is_reported_as_timeout()
    {
        var handler = new StubHandler(async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return await JsonResponse("""{"clauses":[]}""")(cancellationToken);
        });
        var client = CreateClient(handler, timeoutSeconds: 1);

        await Should.ThrowAsync<AiTimeoutException>(() => client.CompleteAsync(Request()));
    }

    [Fact]
    public async Task Missing_credential_fails_before_calling_the_provider()
    {
        var handler = new StubHandler(JsonResponse("""{"clauses":[]}"""));
        var options = new AzureAiOptions
        {
            Endpoint = new Uri("https://example.test/"),
            Deployment = "clauses",
            TimeoutSeconds = 5,
        };
        var client = new AzureFoundryAiModelClient(new HttpClient(handler), options);

        await Should.ThrowAsync<AiConfigurationException>(() => client.CompleteAsync(Request()));
        handler.Bodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task Fake_client_records_requests_and_returns_the_scripted_answer()
    {
        using var document = JsonDocument.Parse("""{"clauses":[]}""");
        var fake = new FakeAiModelClient().Enqueue(new AiCompletionResult(
            document.RootElement.Clone(),
            "model",
            "clauses",
            1,
            2,
            TimeSpan.FromMilliseconds(5)));

        var result = await fake.CompleteAsync(Request());

        result.Deployment.ShouldBe("clauses");
        fake.Requests.Count.ShouldBe(1);
        fake.Requests[0].JsonSchema.ShouldBe(Schema);
    }

    private static AzureFoundryAiModelClient CreateClient(StubHandler handler, int timeoutSeconds = 5)
    {
        var options = new AzureAiOptions
        {
            Endpoint = new Uri("https://example.test/"),
            Deployment = "clauses",
            ApiKey = TestValue,
            TimeoutSeconds = timeoutSeconds,
            MaxAttempts = 3,
            UseJsonSchema = false,
        };

        return new AzureFoundryAiModelClient(new HttpClient(handler), options);
    }

    private static Func<CancellationToken, Task<HttpResponseMessage>> JsonResponse(
        string content,
        int input = 10,
        int output = 5)
    {
        return _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content } } },
                    usage = new { prompt_tokens = input, completion_tokens = output },
                    model = "mistral-large-3",
                }),
                Encoding.UTF8,
                "application/json"),
        });
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses;

        public List<string> Bodies { get; } = [];

        public StubHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] responses)
        {
            _responses = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted HTTP response left.");
            }

            return await _responses.Dequeue()(cancellationToken);
        }
    }
}
