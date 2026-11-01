using Convenciones.Worker;

namespace Convenciones.Worker.Extraction;

public sealed record StoredDocument(string StoragePath, string FileName);

public interface IClauseStore
{
    Task<StoredDocument?> LoadDocumentAsync(Guid runId, CancellationToken cancellationToken);

    Task UpdateProgressAsync(Guid runId, string stage, int clauseCount, CancellationToken cancellationToken);

    Task SaveClausesAsync(ProcessingJob job, IReadOnlyList<MergedClause> clauses, CancellationToken cancellationToken);
}
