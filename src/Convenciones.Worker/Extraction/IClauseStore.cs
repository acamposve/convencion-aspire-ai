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

    Task UpdateProgressAsync(Guid runId, string stage, int clauseCount, CancellationToken cancellationToken);

    Task SaveClausesAsync(ProcessingJob job, IReadOnlyList<MergedClause> clauses, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredClause>> LoadClausesAsync(Guid runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ClauseCategoryInfo>> LoadActiveCategoriesAsync(CancellationToken cancellationToken);

    Task SaveClassificationsAsync(
        ProcessingJob job,
        IReadOnlyList<ClassificationAssignment> assignments,
        int unclassifiedCount,
        CancellationToken cancellationToken);
}
