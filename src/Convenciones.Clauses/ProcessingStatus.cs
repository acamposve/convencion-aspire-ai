namespace Convenciones.Clauses;

/// <summary>Processing state of a run or attempt. Mirrors <c>platform.processing_status</c>.</summary>
public enum ProcessingStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Human review state of an extracted clause. Mirrors <c>platform.clause_review_status</c>.</summary>
public enum ClauseReviewStatus
{
    Pending,
    Accepted,
    Corrected,
    Rejected,
}
