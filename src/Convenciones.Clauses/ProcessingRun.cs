namespace Convenciones.Clauses;

/// <summary>One processing run of a document. Mirrors <c>platform.processing_run</c>.</summary>
public sealed class ProcessingRun
{
    private ProcessingRun()
    {
    }

    public Guid Id { get; private set; }

    public Guid DocumentId { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid? RequestedBy { get; private set; }

    public ProcessingStatus Status { get; private set; }

    public string Stage { get; private set; } = string.Empty;

    public string? Provider { get; private set; }

    public string? Model { get; private set; }

    public string? PromptVersion { get; private set; }

    public int Attempts { get; private set; }

    public int ClauseCount { get; private set; }

    public int ClassifiedCount { get; private set; }

    public int UnclassifiedCount { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset AvailableAt { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
}
