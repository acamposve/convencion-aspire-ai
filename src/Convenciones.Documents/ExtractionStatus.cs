namespace Convenciones.Documents;

/// <summary>
/// Processing state of a document. Mirrors the PostgreSQL type <c>platform.extraction_status</c>.
/// </summary>
/// <remarks>
/// There is deliberately no <c>NotSupported</c> member: the database type does not define one, and
/// since the API accepts PDFs only, no document can be "unsupported" — every stored document is
/// extractable in principle. A format that is not accepted has no record at all.
/// </remarks>
public enum ExtractionStatus
{
    NotRequested,
    Requested,
    InProgress,
    Completed,
    Failed,
}
