namespace Convenciones.Ai;

/// <summary>
/// Scripted model for tests and for local runs without a provider.
/// </summary>
/// <remarks>
/// The queue is deterministic and records every request, so a test can assert both what the business
/// code asked and what it did with the answer without any network call.
/// </remarks>
public sealed class FakeAiModelClient : IAiModelClient
{
    private readonly Queue<Func<AiCompletionRequest, AiCompletionResult>> _responses = new();

    public List<AiCompletionRequest> Requests { get; } = [];

    public FakeAiModelClient Enqueue(AiCompletionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _responses.Enqueue(_ => result);
        return this;
    }

    public FakeAiModelClient Enqueue(Func<AiCompletionRequest, AiCompletionResult> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _responses.Enqueue(factory);
        return this;
    }

    public Task<AiCompletionResult> CompleteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Add(request);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("FakeAiModelClient has no scripted response left.");
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }
}
