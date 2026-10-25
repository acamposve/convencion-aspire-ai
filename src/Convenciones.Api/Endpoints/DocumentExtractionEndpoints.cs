using Convenciones.Api.Infrastructure;
using Convenciones.Documents;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// Document text extraction and extraction status.
/// </summary>
/// <remarks>
/// <c>docs/architecture/api-contracts.md §4.8</c> and <c>docs/tasks/task-05-document-processing.md</c>
/// are the contract. Extraction is performed synchronously inside the request and answers <c>200 OK</c>
/// with the final state: there is no queue or hosted worker in the MVP, so returning 202 for work that
/// has already finished would misreport the document. The 202 + polling design remains the documented
/// path for when extraction becomes genuinely asynchronous.
/// </remarks>
public static class DocumentExtractionEndpoints
{
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

        if (await DocumentAccess.FindAccessibleAsync(session, id, db, cancellationToken).ConfigureAwait(false)
            is not { } document)
        {
            return NotFound(id);
        }

        if (document.ExtractionStatus is ExtractionStatus.Completed)
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
            await RemoveExtractedTextAsync(db, document.Id, cancellationToken).ConfigureAwait(false);
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

            // A failure must not leave stale text behind from a previous attempt, and a missing file
            // is a failure like any other: otherwise obsolete content would still be readable.
            await RemoveExtractedTextAsync(db, document.Id, cancellationToken).ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToExtraction(document, null));
        }

        var completedAt = DateTimeOffset.UtcNow;

        // One document has one text row. Replacing any existing row keeps that invariant even when an
        // operator requeued a completed document; inserting would otherwise fail on the primary key.
        await RemoveExtractedTextAsync(db, document.Id, cancellationToken).ConfigureAwait(false);

        var text = DocumentText.For(document.Id, result.Text!, completedAt);
        db.DocumentTexts.Add(text);

        document.CompleteExtraction(completedAt);
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

        if (await DocumentAccess.FindAccessibleAsync(session, id, db, cancellationToken).ConfigureAwait(false)
            is not { } document)
        {
            return NotFound(id);
        }

        var charCount = await db.DocumentTexts
            .AsNoTracking()
            .Where(text => text.DocumentId == id)
            .Select(text => (int?)text.CharCount)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(ToExtraction(document, charCount));
    }

    /// <summary>Removes any previously extracted text, so an obsolete result cannot be returned.</summary>
    private static Task<int> RemoveExtractedTextAsync(
        ConvencionesDbContext db,
        Guid documentId,
        CancellationToken cancellationToken) =>
        db.DocumentTexts
            .Where(text => text.DocumentId == documentId)
            .ExecuteDeleteAsync(cancellationToken);

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
