using Convenciones.Api.Infrastructure;
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

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/documents", UploadAsync)
            .RequiresCapability("document.write")
            .DisableAntiforgery()
            .WithTags("Documents")
            .WithName("UploadDocument");

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
            var key = DocumentKeys.For(companyId, documentId, DateTimeOffset.UtcNow);

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
                companyId);

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
