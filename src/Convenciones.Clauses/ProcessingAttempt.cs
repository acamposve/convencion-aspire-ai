namespace Convenciones.Clauses;

/// <summary>One attempt inside a run. Mirrors <c>platform.processing_attempt</c>.</summary>
public sealed class ProcessingAttempt
{
    private ProcessingAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid RunId { get; private set; }

    public Guid CompanyId { get; private set; }

    public int AttemptNo { get; private set; }

    public ProcessingStatus Status { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public int? LatencyMs { get; private set; }

    public string? ProviderRequestId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }
}
