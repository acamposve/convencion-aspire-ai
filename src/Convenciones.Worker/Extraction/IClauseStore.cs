using Convenciones.Ai;
using Convenciones.Worker;

namespace Convenciones.Worker.Extraction;

public sealed record StoredDocument(string StoragePath, string FileName);

public sealed record StoredClause(Guid Id, int Ordinal, string? Number, string? Heading, string Text);

public sealed record ClauseCategoryInfo(Guid Id, string Key, string Name, string? Description);

public sealed record ClassificationAssignment(
    Guid ClauseId,
    Guid CategoryId,
    double Confidence,
    bool IsPrimary,
    string? Rationale);

public interface IClauseStore
{
    Task<StoredDocument?> LoadDocumentAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Reports progress for the job's own attempt. The write is fenced by run, attempt number and
    /// attempt id; a stale worker gets <see cref="StaleProcessingAttemptException"/> and changes nothing.
    /// </summary>
    Task UpdateProgressAsync(ProcessingJob job, string stage, int clauseCount, CancellationToken cancellationToken);

    Task SaveClausesAsync(ProcessingJob job, IReadOnlyList<MergedClause> clauses, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredClause>> LoadClausesAsync(Guid runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ClauseCategoryInfo>> LoadActiveCategoriesAsync(CancellationToken cancellationToken);

    Task SaveClassificationsAsync(
        ProcessingJob job,
        IReadOnlyList<ClassificationAssignment> assignments,
        int unclassifiedCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records one provider invocation: tokens as reported, the internally estimated cost and the price
    /// list used. Written as soon as the call returns, before parsing or persistence, so a later failure
    /// in the same stage cannot lose a charge the provider already booked.
    /// </summary>
    /// <param name="invocationId">
    /// Durable identity of this logical provider call, generated once before submission. A replay of the
    /// same insert reuses it; a second call always carries a new one.
    /// </param>
    Task RecordInvocationAsync(
        ProcessingJob job,
        string stage,
        Guid invocationId,
        int invocationNo,
        AiCompletionResult result,
        int pages,
        AiPricing pricing,
        CancellationToken cancellationToken);
}
