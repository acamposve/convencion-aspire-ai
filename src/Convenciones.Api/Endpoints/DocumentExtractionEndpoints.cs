using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Convenciones.Documents;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// Document upload and text extraction. PDF only, 20 MiB maximum, private storage, server-generated keys.
/// </summary>
/// <remarks>
/// <c>docs/architecture/api-contracts.md §4.8</c> and <c>docs/tasks/task-04</c>/<c>task-05</c> are the
/// contract. Extraction is performed synchronously inside the request: there is no queue in the MVP, and
/// returning 202 for work already finished would be a lie about the state of the document.
/// </remarks>
public static class DocumentExtractionEndpoints
{
    /// <summary>20 MiB, exactly the value of the <c>document_size_within_limit</c> constraint.</summary>
    

    /// <summary>Bounded retries: an unbounded loop would look like activity and hide a broken document.</summary>
    public const int MaxExtractionAttempts = 3;

    public static IEndpointRouteBuilder MapDocumentExtractionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/documents/{id:guid}/extract", ExtractAsync)
            .RequiresCapability("document.write")
            .WithTags("Documents")
            .WithName("ExtractDocument");

        app.MapGet("/documents/{id:guid}/extraction", GetExtractionAsync)
            .RequiresCapability("document.read")
            .WithTags("Documents")
            .WithName("GetDocumentExtraction");

        return app;
    }

    /// <summary>
    /// Runs extraction for one document.
    /// </summary>
    /// <remarks>
    /// The three outcomes are distinct and all are recorded: success stores the text, "the PDF has no
    /// text" is a failure with a reason (not an empty success), and any error is a failure with a
    /// reason. Extraction is refused for an already-completed document and once the attempt limit is
    /// reached — 409 in both cases, because the request is not malformed, the state is wrong.
    /// </remarks>
    private static async Task<IResult> ExtractAsync(
        Guid id,
        ConvencionesDbContext db,
        IDocumentStorage storage,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        if (!CanAccess(session, id, db, out var document))
        {
            return NotFound(id);
        }

        if (document!.ExtractionStatus is ExtractionStatus.Completed)
        {
            return State("This document has already been processed.");
        }

        if (document.ExtractionAttempts >= MaxExtractionAttempts)
        {
            return State($"Extraction has already been attempted {MaxExtractionAttempts} times.");
        }

        var now = DateTimeOffset.UtcNow;
        document.BeginExtraction(now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var stream = await storage.OpenReadAsync(document.StoragePath, cancellationToken)
            .ConfigureAwait(false);

        if (stream is null)
        {
            document.FailExtraction("The stored file could not be found.", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToExtraction(document, null));
        }

        TextExtractionResult result;
        await using (stream.ConfigureAwait(false))
        {
            result = PdfTextExtractor.Extract(stream);
        }

        if (!result.Succeeded)
        {
            document.FailExtraction(result.FailureReason!, DateTimeOffset.UtcNow);

            // A failure must not leave stale text behind from a previous attempt.
            await db.DocumentTexts
                .Where(text => text.DocumentId == document.Id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToExtraction(document, null));
        }

        var text = DocumentText.For(document.Id, result.Text!);
        db.DocumentTexts.Add(text);

        document.CompleteExtraction(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToExtraction(document, text.CharCount));
    }

    private static async Task<IResult> GetExtractionAsync(
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

        if (!CanAccess(session, id, db, out var document))
        {
            return NotFound(id);
        }

        var charCount = await db.DocumentTexts
            .AsNoTracking()
            .Where(text => text.DocumentId == id)
            .Select(text => (int?)text.CharCount)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(ToExtraction(document!, charCount));
    }

    /// <summary>
    /// Loads a document the caller may touch: their company's, or any company's when they hold
    /// <c>company.read_all</c>. A document belonging to another company is reported as not found.
    /// </summary>
    private static bool CanAccess(
        SessionContext session,
        Guid documentId,
        ConvencionesDbContext db,
        out Document? document)
    {
        document = db.Documents.SingleOrDefault(candidate => candidate.Id == documentId);

        if (document is null)
        {
            return false;
        }

        if (document.CompanyId is { } companyId && TenantScope.CanAccess(session, companyId))
        {
            return true;
        }

        document = null;
        return false;
    }

    private static ExtractionResponse ToExtraction(Document document, int? charCount) =>
        new(
            document.Id,
            document.ExtractionStatus.ToString(),
            document.ExtractionAttempts,
            document.ExtractionStartedAt,
            document.ExtractionCompletedAt,
            document.ExtractionError,
            charCount);

    private static IResult Forbidden(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Forbidden });

    private static IResult NotFound(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No document with id '{id}' is visible to the caller.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult State(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Invalid state",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.InvalidState });
}

public sealed record ExtractionResponse(
    Guid Id,
    string Status,
    int Attempts,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    int? CharCount);
