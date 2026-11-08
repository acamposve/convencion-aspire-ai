using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Convenciones.Clauses;
using Convenciones.Documents;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// Document upload. PDF only, 20 MiB maximum, private storage, server-generated keys.
/// </summary>
/// <remarks>
/// <c>docs/architecture/api-contracts.md §4.8</c> and <c>docs/tasks/task-04-document-upload.md</c> are
/// the contract. The order of checks matters: nothing reaches storage until the bytes have been proven
/// to be a PDF and the caller has been proven to have a company.
/// </remarks>
public static partial class DocumentEndpoints
{
    /// <summary>20 MiB, exactly the value of the <c>document_size_within_limit</c> constraint.</summary>
    public const long MaxDocumentBytes = 20L * 1024 * 1024;

    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/documents", UploadAsync)
            .RequiresCapability("document.write")
            .DisableAntiforgery()
            .WithTags("Documents")
            .WithName("UploadDocument");

        app.MapGet("/documents", ListAsync)
            .RequiresCapability("document.read")
            .WithTags("Documents")
            .WithName("ListDocuments");

        app.MapGet("/documents/{id:guid}", GetAsync)
            .RequiresCapability("document.read")
            .WithTags("Documents")
            .WithName("GetDocument");

        app.MapGet("/documents/{id:guid}/text", GetTextAsync)
            .RequiresCapability("document.read")
            .WithTags("Documents")
            .WithName("GetDocumentText");

        app.MapGet("/documents/{id:guid}/download", DownloadAsync)
            .RequiresCapability("document.read")
            .WithTags("Documents")
            .WithName("DownloadDocument");

        app.MapDelete("/documents/{id:guid}", DeleteAsync)
            .RequiresCapability("document.write")
            .WithTags("Documents")
            .WithName("DeleteDocument");

        return app;
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        ConvencionesDbContext db,
        IDocumentStorage storage,
        ISessionAccessor accessor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("DocumentUpload");
        var session = accessor.Session;

        if (session is null)
        {
            return Problem(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden,
                "No session was resolved for this request.");
        }

        if (session.CompanyId is not { } companyId)
        {
           	return Problem(StatusCodes.Status409Conflict, ErrorCodes.InvalidState,
                "A document must belong to a company, and this account has none.");
        }

        IFormFile file;
        try
        {
            if (!request.HasFormContentType)
            {
                return Problem(StatusCodes.Status415UnsupportedMediaType, ErrorCodes.ValidationFailed,
                    "The request must be multipart/form-data with a single 'file' part.");
            }

            var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            var files = form.Files.Where(candidate => candidate.Name == "file").ToArray();

            if (files.Length != 1)
            {
                return Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed,
                    "Exactly one 'file' part is required.");
            }

            file = files[0];
        }
        catch (InvalidDataException)
        {
            // The multipart reader refuses a body over MultipartBodyLengthLimit.
            return Problem(StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"The upload exceeds the maximum of {MaxDocumentBytes} bytes (20 MiB).");
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"The upload exceeds the maximum of {MaxDocumentBytes} bytes (20 MiB).");
        }

        if (file.Length == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed,
                "The file is empty.");
        }

        if (file.Length > MaxDocumentBytes)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"The upload exceeds the maximum of {MaxDocumentBytes} bytes (20 MiB).");
        }

        // The bytes are staged in a temporary file so that validation and storage read the same
        // content without holding 20 MiB in the managed heap, and so that the size is verified as the
        // stream is consumed rather than trusted from the part's declared length.
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"convenciones-upload-{Guid.NewGuid():N}.tmp");
        string checksum;
        long actualSize;

        try
        {
            (checksum, actualSize) = await StageAsync(file, temporaryPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PayloadTooLargeException)
        {
            TryDelete(temporaryPath);
            return Problem(StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"The upload exceeds the maximum of {MaxDocumentBytes} bytes (20 MiB).");
        }

        try
        {
            PdfValidationResult validation;

            await using (var staged = File.OpenRead(temporaryPath))
            {
                validation = PdfValidator.Validate(staged);
            }

            if (validation.Outcome is PdfValidationOutcome.WrongFormat)
            {
                logger.LogInformation("Rejected upload {FileName}: wrong format.", file.FileName);
                return Problem(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type",
                    validation.Reason ?? "Only PDF documents are accepted.");
            }

            if (validation.Outcome is PdfValidationOutcome.Malformed)
            {
                logger.LogInformation("Rejected upload {FileName}: malformed PDF.", file.FileName);
                return Problem(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed,
                    validation.Reason ?? "The PDF could not be read.");
            }

            var documentId = Guid.NewGuid();
            var uploadedAt = DateTimeOffset.UtcNow;
            var key = DocumentKeys.For(companyId, documentId, uploadedAt);

            // Store first, then insert: an orphaned object is invisible and sweepable, whereas a row
            // pointing at a missing object is a broken record that looks valid.
            await using (var staged = File.OpenRead(temporaryPath))
            {
                await storage.SaveAsync(key, staged, cancellationToken).ConfigureAwait(false);
            }

            var document = Document.Register(
                documentId,
                key,
                SanitiseFileName(file.FileName),
                actualSize,
                checksum,
                session.UserId,
                companyId,
                uploadedAt);

            db.Documents.Add(document);

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception)
            {
                // Compensating action: without it, every failed insert would leak a 20 MiB object.
                logger.LogError(exception, "Document insert failed; removing the stored object {Key}.", key);
                await storage.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);

                return Problem(StatusCodes.Status500InternalServerError, ErrorCodes.InternalError,
                    "The document could not be recorded.");
            }

            logger.LogInformation("Stored document {DocumentId} for company {CompanyId}.", documentId, companyId);

            return Results.Created(
                new Uri($"/documents/{documentId}", UriKind.Relative),
                new DocumentResponse(
                    document.Id,
                    document.FileName,
                    document.ContentType,
                    document.SizeBytes,
                    document.ChecksumSha256,
                    document.ExtractionStatus.ToString(),
                    document.CreatedAt));
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    /// <summary>
    /// Copies the upload to a temporary file, refusing as soon as the limit is exceeded.
    /// </summary>
    private static async Task<(string Checksum, long Size)> StageAsync(
        IFormFile file,
        string path,
        CancellationToken cancellationToken)
    {
        using var hasher = System.Security.Cryptography.SHA256.Create();
        long total = 0;

        await using var source = file.OpenReadStream();
        await using var target = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;

            if (total > MaxDocumentBytes)
            {
                throw new PayloadTooLargeException();
            }

            hasher.TransformBlock(buffer, 0, read, null, 0);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        hasher.TransformFinalBlock([], 0, 0);

        return (Convert.ToHexStringLower(hasher.Hash!), total);
    }

    private static string SanitiseFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            return "document.pdf";
        }

        return name.Length > 255 ? name[..255] : name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temporary file is not worth failing the request over.
        }
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(
            statusCode: status,
            title: status is 413 ? "Content too large" : "Request rejected",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    // ---- Retrieval -----------------------------------------------------------------------------

    /// <summary>
    /// Lists the documents the caller may see, newest first, with offset paging.
    /// </summary>
    /// <remarks>
    /// The company filter is part of the query, not applied after materialising, so the total is the
    /// number of rows the caller may see rather than the number of rows that exist. A caller with no
    /// company and no <c>company.read_all</c> gets an empty page, never an unfiltered one: absence of
    /// a company must not become visibility of everything.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        var query = db.Documents.AsNoTracking();

        if (session.HasCapability(TenantScope.CrossCompanyCapability))
        {
            // A document with no company is visible to nobody through the API (T1); read_all does
            // not change that, so those rows are excluded here as well.
            query = query.Where(document => document.CompanyId != null);
        }
        else if (session.CompanyId is { } ownCompany)
        {
            query = query.Where(document => document.CompanyId == ownCompany);
        }
        else
        {
            return Results.Ok(new DocumentListResponse([], page, pageSize, 0));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await query
            .OrderByDescending(document => document.UploadedAt)
            .ThenByDescending(document => document.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new DocumentListResponse(
            rows.Select(ToSummary).ToArray(), page, pageSize, total));
    }

    /// <summary>
    /// Returns one document's metadata and extraction state, or 404.
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var document = await DocumentAccess
            .FindAccessibleAsync(session, id, db, cancellationToken)
            .ConfigureAwait(false);

        return document is null ? NotFound(id) : Results.Ok(ToDetail(document));
    }

    /// <summary>
    /// Returns the extracted text, or 404 when there is none.
    /// </summary>
    /// <remarks>
    /// "No text" is a missing row, never an empty string: the database forbids a blank
    /// <c>document_text</c> row, and reporting an empty body would look like a successful extraction
    /// that found nothing.
    /// </remarks>
    private static async Task<IResult> GetTextAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var document = await DocumentAccess
            .FindAccessibleAsync(session, id, db, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return NotFound(id);
        }

        var text = await db.DocumentTexts
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.DocumentId == id, cancellationToken)
            .ConfigureAwait(false);

        return text is null
            ? NotFoundText(id)
            : Results.Ok(new DocumentTextResponse(
                document.Id, text.Content, text.CharCount, text.ExtractedAt));
    }

    /// <summary>
    /// Streams the stored bytes. Range requests are enabled, so a client can resume a download.
    /// </summary>
    /// <remarks>
    /// The bytes are served by the API after the tenant check. The response never contains the
    /// storage key or a signed URL that outlives the request, and the private directory is not
    /// reachable by path.
    /// </remarks>
    private static async Task<IResult> DownloadAsync(
        Guid id,
        HttpContext context,
        ConvencionesDbContext db,
        IDocumentStorage storage,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var document = await DocumentAccess
            .FindAccessibleAsync(session, id, db, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return NotFound(id);
        }

        var stream = await storage.OpenReadAsync(document.StoragePath, cancellationToken)
            .ConfigureAwait(false);

        if (stream is null)
        {
            // A row pointing at missing bytes is not a usable document. Reporting 404 keeps "gone"
            // and "never existed" indistinguishable, like every other not-visible case.
            return NotFound(id);
        }

        context.Response.Headers.CacheControl = "private, no-store";

        return Results.File(
            stream,
            document.ContentType,
            document.FileName,
            enableRangeProcessing: true);
    }

    /// <summary>
    /// Soft-deletes a document: the row and its audit trail remain, the bytes are removed.
    /// </summary>
    /// <remarks>
    /// <c>If-Match</c> is required, like every other DELETE in the contract. The document is located
    /// before the header is read, so another company's document is 404 rather than 400 even when the
    /// caller omits it. The row is hidden before the object is removed: a failed object removal
    /// leaves an invisible orphan, whereas removing bytes first would leave a visible, broken record.
    /// </remarks>
    private static async Task<IResult> DeleteAsync(
        Guid id,
        HttpRequest request,
        ConvencionesDbContext db,
        IDocumentStorage storage,
        ISessionAccessor accessor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var document = await DocumentAccess
            .FindAccessibleAsync(session, id, db, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return NotFound(id);
        }

        await using var deleteTransaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockDocumentAsync(db, id, cancellationToken).ConfigureAwait(false);

        // Reload after the stable document lock: the row checked for If-Match must be the row that
        // is soft-deleted, not a snapshot read before another writer.
        db.Entry(document).State = EntityState.Detached;
        document = await DocumentAccess
            .FindAccessibleAsync(session, id, db, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
        {
            return NotFound(id);
        }

        var ifMatch = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch) || !TryParseRowVersion(ifMatch, out var expectedVersion))
        {
            return Validation(
                "The If-Match header with the current rowVersion is required to delete a document.");
        }

        if (document.RowVersion != expectedVersion)
        {
            return ConcurrentModification();
        }

        document.SoftDelete(DateTimeOffset.UtcNow);

        // Cancelling active runs belongs to the same document transaction as the soft delete:
        // otherwise a run can remain Queued/Running after its document is gone and can never be
        // claimed or completed.
        await CancelActiveRunsAsync(db, id, session.UserId, cancellationToken).ConfigureAwait(false);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await deleteTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await deleteTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return ConcurrentModification();
        }

        try
        {
            await storage.DeleteAsync(document.StoragePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The document is already invisible and the attempt is audited. Failing the request now
            // would report a failure for an operation the caller can no longer observe; the leftover
            // object is private, unreachable and sweepable.
            loggerFactory
                .CreateLogger("DocumentDelete")
                .LogError(exception,
                    "Document {DocumentId} is hidden, but its stored object could not be removed.", id);
        }

        return Results.NoContent();
    }

    private static Task CancelActiveRunsAsync(
        ConvencionesDbContext db,
        Guid documentId,
        Guid actorId,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            """
            with active as (
                select id, requested_by, attempts, status::text as previous_status
                from platform.processing_run
                where document_id = {0}
                  and status in (
                      'Queued'::platform.processing_status,
                      'Running'::platform.processing_status)
                for update
            ), updated as (
                update platform.processing_run r
                set status = 'Cancelled'::platform.processing_status,
                    stage = 'Cancelled',
                    finished_at = now(),
                    lease_expires_at = null,
                    error_code = 'document_deleted',
                    error_message = 'The document was deleted while the run was active.',
                    updated_at = now()
                from active a
                where r.id = a.id
                returning r.id
            ), attempts as (
                update platform.processing_attempt p
                set status = 'Cancelled'::platform.processing_status,
                    error_code = 'document_deleted',
                    error_message = 'The document was deleted while the run was active.',
                    finished_at = now()
                from active a
                where p.run_id = a.id
                  and p.attempt_no = a.attempts
                  and p.status = 'Running'::platform.processing_status
                returning p.id
            ), audited as (
                insert into platform.audit_entry
                    (actor_id, module, entity_type, entity_id, action, from_state, to_state, changes)
                select coalesce(a.requested_by, {1}),
                       'documents',
                       'processing_run',
                       a.id::text,
                       'processing.cancelled',
                       a.previous_status,
                       'Cancelled',
                       jsonb_build_object('code', 'document_deleted')
                from active a
            )
            select (select count(*) from updated) + (select count(*) from attempts) * 0;
            """,
            [documentId, actorId],
            cancellationToken);

    private static Task LockDocumentAsync(
        ConvencionesDbContext db,
        Guid documentId,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "select pg_advisory_xact_lock(hashtextextended({0}, 0))",
            [ClassificationLocks.DocumentKey(documentId)],
            cancellationToken);

    private static DocumentSummary ToSummary(Document document) =>
        new(
            document.Id,
            document.FileName,
            document.ContentType,
            document.SizeBytes,
            document.ExtractionStatus.ToString(),
            document.ExtractionAttempts,
            document.UploadedAt,
            document.ExtractionError);

    private static DocumentDetail ToDetail(Document document) =>
        new(
            document.Id,
            document.FileName,
            document.ContentType,
            document.SizeBytes,
            document.ExtractionStatus.ToString(),
            document.ExtractionAttempts,
            document.ExtractionStartedAt,
            document.ExtractionCompletedAt,
            document.ExtractionError,
            document.UploadedAt,
            document.RowVersion);

    private static bool TryParseRowVersion(string header, out uint version) =>
        uint.TryParse(header.Trim().Trim('"'), out version);

    private static IResult Forbidden(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Forbidden });

    private static IResult Validation(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Validation failed",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ValidationFailed });

    private static IResult NotFound(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No document with id '{id}' is visible to the caller.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult NotFoundText(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No extracted text exists for document '{id}'.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult ConcurrentModification() =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "The document was modified by someone else. Re-read it and retry with the current rowVersion.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ConcurrentModification });
}

public sealed class PayloadTooLargeException : Exception
{
    public PayloadTooLargeException()
        : base("The upload exceeded the maximum size.")
    {
    }
}

public sealed record DocumentResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string? ChecksumSha256,
    string ExtractionStatus,
    DateTimeOffset CreatedAt);

/// <summary>List projection: enough for the documents screen, no storage or uploader internals.</summary>
/// <remarks>
/// <c>ExtractionError</c> is included because the documents screen must show why a document failed
/// without a request per row. It is a server-authored explanation, not an exception message.
/// </remarks>
public sealed record DocumentSummary(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string ExtractionStatus,
    int ExtractionAttempts,
    DateTimeOffset UploadedAt,
    string? ExtractionError);

/// <summary>One document's metadata, extraction state and concurrency token.</summary>
public sealed record DocumentDetail(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string ExtractionStatus,
    int ExtractionAttempts,
    DateTimeOffset? ExtractionStartedAt,
    DateTimeOffset? ExtractionCompletedAt,
    string? ExtractionError,
    DateTimeOffset UploadedAt,
    uint RowVersion);

public sealed record DocumentListResponse(
    IReadOnlyList<DocumentSummary> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record DocumentTextResponse(
    Guid DocumentId,
    string Content,
    int CharCount,
    DateTimeOffset ExtractedAt);
