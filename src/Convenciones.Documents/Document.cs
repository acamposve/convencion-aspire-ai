using Convenciones.Catalog;

namespace Convenciones.Documents;

/// <summary>
/// A stored document. Mirrors <c>platform.document</c>.
/// </summary>
public sealed class Document : IAuditable
{
    private Document()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Server-generated. Never the client filename.</summary>
    public string StoragePath { get; private set; } = string.Empty;

    /// <summary>Metadata only. Never used as a path.</summary>
    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    public string? ChecksumSha256 { get; private set; }

    public Guid? UploadedBy { get; private set; }

    public Guid? CompanyId { get; private set; }

    /// <summary>
    /// Processing status, stored in the PostgreSQL enum <c>platform.extraction_status</c>.
    /// </summary>
    public ExtractionStatus ExtractionStatus { get; private set; } = ExtractionStatus.NotRequested;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the object was accepted into storage. The list is ordered by this column.</summary>
    public DateTimeOffset UploadedAt { get; private set; }

    /// <summary>
    /// Soft delete marker. A non-null value makes the document invisible to every read, including
    /// download and text retrieval; the row and its audit trail remain.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>), used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    public int ExtractionAttempts { get; private set; }

    public DateTimeOffset? ExtractionStartedAt { get; private set; }

    public DateTimeOffset? ExtractionCompletedAt { get; private set; }

    public string? ExtractionError { get; private set; }

    /// <summary>Starts an attempt. Called only after the retry limit has been checked.</summary>
    public void BeginExtraction(DateTimeOffset now)
    {
        ExtractionAttempts++;
        ExtractionStartedAt = now;
        ExtractionCompletedAt = null;
        ExtractionError = null;
        ExtractionStatus = ExtractionStatus.InProgress;
    }

    public void CompleteExtraction(DateTimeOffset now)
    {
        ExtractionStatus = ExtractionStatus.Completed;
        ExtractionCompletedAt = now;
        ExtractionError = null;
    }

    /// <summary>
    /// Records a failure. The reason is kept because an operator needs to tell "no text" from
    /// "damaged", and the API never reports a failure as a success.
    /// </summary>
    public void FailExtraction(string reason, DateTimeOffset now)
    {
        ExtractionStatus = ExtractionStatus.Failed;
        ExtractionCompletedAt = now;
        ExtractionError = reason;
    }

    public void SetExtractionStatus(ExtractionStatus status) => ExtractionStatus = status;

    /// <summary>
    /// Marks the document deleted without removing the row. The bytes are removed separately by the
    /// caller; the record and its audit trail stay for history.
    /// </summary>
    public void SoftDelete(DateTimeOffset now)
    {
        if (DeletedAt is not null)
        {
            throw new InvalidOperationException("The document is already deleted.");
        }

        DeletedAt = now;
    }

    public string AuditModule => "documents";

    public string AuditEntityType => "document";

    public string AuditEntityId => Id.ToString();

    public static Document Register(
        Guid id,
        string storagePath,
        string fileName,
        long sizeBytes,
        string checksumSha256,
        Guid uploadedBy,
        Guid companyId,
        DateTimeOffset uploadedAt) =>
        new()
        {
            Id = id,
            StoragePath = storagePath,
            FileName = fileName,
            ContentType = "application/pdf",
            SizeBytes = sizeBytes,
            ChecksumSha256 = checksumSha256,
            UploadedBy = uploadedBy,
            CompanyId = companyId,
            CreatedAt = uploadedAt,
            UploadedAt = uploadedAt,
        };
}

/// <summary>
/// Produces the storage key. Server-generated, derived from the company and a new document id, so no
/// client-supplied text can reach a path.
/// </summary>
public static class DocumentKeys
{
    public static string For(Guid companyId, Guid documentId, DateTimeOffset now) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{companyId:N}/{now:yyyy}/{now:MM}/{documentId:N}.pdf");
}
