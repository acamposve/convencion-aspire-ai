namespace Convenciones.Worker;

/// <summary>A claimed run. No document text is carried: the stage reads it from storage.</summary>
public sealed record ProcessingJob(
    Guid RunId,
    Guid DocumentId,
    Guid CompanyId,
    int Attempt,
    Guid AttemptId = default);

/// <summary>
/// The pipeline the worker executes. P4/P5 implement it; the worker only owns lifecycle, retries and
/// persistence of the terminal state.
/// </summary>
public interface IProcessingStage
{
    Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken);
}

/// <summary>The worker no longer owns the attempt it is trying to persist.</summary>
public sealed class StaleProcessingAttemptException(Guid runId, Guid attemptId)
    : Exception($"Run '{runId}' is no longer owned by attempt '{attemptId}'.")
{
    public Guid RunId { get; } = runId;
    public Guid AttemptId { get; } = attemptId;
    public string Code => "stale_attempt";
}

/// <summary>A failure that must not be retried (unsupported document, exhausted attempts, …).</summary>
public sealed class PermanentProcessingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The document was soft-deleted after the run was claimed.</summary>
public sealed class DocumentDeletedException(Guid documentId)
    : Exception($"Document '{documentId}' was deleted while the run was active.")
{
    public Guid DocumentId { get; } = documentId;
    public string Code => "document_deleted";
}
