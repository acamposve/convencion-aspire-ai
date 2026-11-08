namespace Convenciones.Clauses;

/// <summary>
/// Canonical advisory-lock keys shared by the API, worker, re-extraction and retention paths.
/// Lock order is always run first, then clause. The keys are namespaced so a run id and a clause id
/// can never collide.
/// </summary>
public static class ClassificationLocks
{
    public static string DocumentKey(Guid documentId) => $"classification-document:{documentId:N}";

    public static string RunKey(Guid runId) => $"classification-run:{runId:N}";

    public static string ClauseKey(Guid clauseId) => $"classification-clause:{clauseId:N}";
}
