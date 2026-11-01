namespace Convenciones.Worker;

/// <summary>A claimed run. No document text is carried: the stage reads it from storage.</summary>
public sealed record ProcessingJob(Guid RunId, Guid DocumentId, Guid CompanyId, int Attempt);

/// <summary>
/// The pipeline the worker executes. P4/P5 implement it; the worker only owns lifecycle, retries and
/// persistence of the terminal state.
/// </summary>
public interface IProcessingStage
{
    Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken);
}

/// <summary>A failure that must not be retried (unsupported document, exhausted attempts, …).</summary>
public sealed class PermanentProcessingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
