using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Convenciones.Clauses;
using Convenciones.Documents;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Convenciones.Api.Endpoints;

/// <summary>Processing runs, extracted clauses, review and the taxonomy read-only surface.</summary>
public static class ProcessingEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapProcessingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/documents/{id:guid}/process", StartAsync)
            .RequiresCapability("clause.process")
            .WithTags("Processing")
            .WithName("StartDocumentProcessing");

        app.MapGet("/documents/{id:guid}/processing", GetLatestAsync)
            .RequiresCapability("clause.read")
            .WithTags("Processing")
            .WithName("GetDocumentProcessing");

        app.MapGet("/processing/{id:guid}", GetRunAsync)
            .RequiresCapability("clause.read")
            .WithTags("Processing")
            .WithName("GetProcessingRun");

        app.MapGet("/documents/{id:guid}/clauses", ListClausesAsync)
            .RequiresCapability("clause.read")
            .WithTags("Processing")
            .WithName("ListDocumentClauses");

        app.MapGet("/clauses/{id:guid}", GetClauseAsync)
            .RequiresCapability("clause.read")
            .WithTags("Processing")
            .WithName("GetClause");

        app.MapPatch("/clauses/{id:guid}", ReviewAsync)
            .RequiresCapability("clause.review")
            .WithTags("Processing")
            .WithName("ReviewClause");

        app.MapGet("/clause-categories", ListCategoriesAsync)
            .RequiresCapability("clause.read")
            .WithTags("Processing")
            .WithName("ListClauseCategories");

        return app;
    }

    private static async Task<IResult> StartAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (await DocumentAccess.FindAccessibleAsync(session, id, db, cancellationToken).ConfigureAwait(false) is not { } document)
        {
            return NotFoundDocument(id);
        }

        if (document.CompanyId is not { } companyId)
        {
            return State("invalid_state", "The document has no company.");
        }

        var hasText = await db.DocumentTexts.AnyAsync(text => text.DocumentId == id, cancellationToken).ConfigureAwait(false);
        if (!hasText)
        {
            return State("text_not_extracted", "Extract the document text before processing it.");
        }

        var existing = await db.ProcessingRuns
            .AsNoTracking()
            .Where(run => run.DocumentId == id
                && (run.Status == ProcessingStatus.Queued || run.Status == ProcessingStatus.Running))
            .OrderByDescending(run => run.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return Results.Accepted($"/processing/{existing.Id}", ToResponse(existing));
        }

        var run = ProcessingRun.Create(id, companyId, session.UserId, DateTimeOffset.UtcNow);
        db.ProcessingRuns.Add(run);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            // Two concurrent clicks: the partial unique index chose one; return that one.
            var winner = await db.ProcessingRuns
                .AsNoTracking()
                .Where(candidate => candidate.DocumentId == id
                    && (candidate.Status == ProcessingStatus.Queued || candidate.Status == ProcessingStatus.Running))
                .OrderByDescending(candidate => candidate.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return winner is null
                ? State("already_processing", "A processing run already exists.")
                : Results.Accepted($"/processing/{winner.Id}", ToResponse(winner!));
        }

        return Results.Accepted($"/processing/{run.Id}", ToResponse(run));
    }

    private static async Task<IResult> GetLatestAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (await DocumentAccess.FindAccessibleAsync(session, id, db, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFoundDocument(id);
        }

        var run = await db.ProcessingRuns
            .AsNoTracking()
            .Where(candidate => candidate.DocumentId == id)
            .OrderByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return run is null ? NotFoundRun(id) : Results.Ok(ToResponse(run));
    }

    private static async Task<IResult> GetRunAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        var run = await db.ProcessingRuns.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return run is null || !TenantScope.CanAccess(session, run.CompanyId)
            ? NotFoundRun(id)
            : Results.Ok(ToResponse(run));
    }

    private static async Task<IResult> ListClausesAsync(
        Guid id,
        int? page,
        int? pageSize,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (await DocumentAccess.FindAccessibleAsync(session, id, db, cancellationToken).ConfigureAwait(false) is null)
        {
            return NotFoundDocument(id);
        }

        var currentPage = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.ExtractedClauses.AsNoTracking().Where(clause => clause.DocumentId == id);

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderBy(clause => clause.Ordinal)
            .Skip((currentPage - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows.Select(ToProcessingClauseResponse).ToArray();
        return Results.Ok(new ProcessingClausePage(items, currentPage, size, total));
    }

    private static async Task<IResult> GetClauseAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        var clause = await db.ExtractedClauses.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (clause is null || !TenantScope.CanAccess(session, clause.CompanyId))
        {
            return NotFoundClause(id);
        }

        var classifications = await db.ClauseClassifications.AsNoTracking()
            .Where(item => item.ClauseId == id)
            .Join(db.ClauseCategories, item => item.CategoryId, category => category.Id,
                (item, category) => new ProcessingClassificationResponse(
                    category.Key,
                    category.Name,
                    (double?)item.Confidence,
                    item.IsPrimary,
                    item.Rationale))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(ToProcessingClauseResponse(clause!) with { Classifications = classifications });
    }

    private static async Task<IResult> ReviewAsync(
        Guid id,
        ReviewClauseRequest request,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (!Enum.TryParse<ClauseReviewStatus>(request.Status, ignoreCase: true, out var status))
        {
            return Problem(StatusCodes.Status400BadRequest, "validation_failed", "Unknown review status.");
        }

        var clause = await db.ExtractedClauses
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (clause is null || !TenantScope.CanAccess(session, clause.CompanyId))
        {
            return NotFoundClause(id);
        }

        clause.Review(status, request.Text, request.Heading);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToProcessingClauseResponse(clause!));
    }

    private static async Task<IResult> ListCategoriesAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        if (accessor.Session is null)
        {
            return Forbidden();
        }

        var categories = await db.ClauseCategories.AsNoTracking()
            .Where(category => category.IsActive)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Name)
            .Select(category => new ClauseCategoryResponse(
                category.Key,
                category.Name,
                category.Description,
                category.ParentId,
                category.SortOrder))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(categories);
    }

    private static ProcessingRunResponse ToResponse(ProcessingRun run) =>
        new(
            run.Id,
            run.DocumentId,
            run.Status.ToString(),
            run.Stage,
            run.Attempts,
            run.ClauseCount,
            run.ClassifiedCount,
            run.UnclassifiedCount,
            run.ErrorCode,
            run.ErrorMessage,
            run.CreatedAt,
            run.StartedAt,
            run.FinishedAt);

    private static ProcessingClauseResponse ToProcessingClauseResponse(ExtractedClause clause) =>
        new(
            clause.Id,
            clause.Ordinal,
            clause.ClauseNumber,
            clause.Heading,
            clause.Text,
            clause.PageFrom,
            clause.PageTo,
            clause.SourceCharStart,
            clause.SourceCharEnd,
            (double?)clause.Confidence,
            clause.NeedsReview,
            clause.ReviewStatus.ToString(),
            []);

    private static IResult Forbidden() =>
        Problem(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "No session was resolved for this request.");

    private static IResult NotFoundDocument(Guid id) =>
        Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"No document with id '{id}' is visible to the caller.");

    private static IResult NotFoundRun(Guid id) =>
        Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"No processing run with id '{id}' is visible to the caller.");

    private static IResult NotFoundClause(Guid id) =>
        Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"No clause with id '{id}' is visible to the caller.");

    private static IResult State(string code, string detail) =>
        Problem(StatusCodes.Status409Conflict, code, detail);

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(
            statusCode: status,
            title: status == StatusCodes.Status404NotFound ? "Not found" : "Invalid state",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record ProcessingRunResponse(
    Guid Id,
    Guid DocumentId,
    string Status,
    string Stage,
    int Attempts,
    int ClauseCount,
    int ClassifiedCount,
    int UnclassifiedCount,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record ProcessingClauseResponse(
    Guid Id,
    int Ordinal,
    string? Number,
    string? Heading,
    string Text,
    int PageFrom,
    int PageTo,
    int? SourceCharStart,
    int? SourceCharEnd,
    double? Confidence,
    bool NeedsReview,
    string ReviewStatus,
    IReadOnlyList<ProcessingClassificationResponse> Classifications);

public sealed record ProcessingClassificationResponse(
    string Key,
    string Name,
    double? Confidence,
    bool IsPrimary,
    string? Rationale);

public sealed record ProcessingClausePage(
    IReadOnlyList<ProcessingClauseResponse> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record ReviewClauseRequest(string Status, string? Text, string? Heading);

public sealed record ClauseCategoryResponse(
    string Key,
    string Name,
    string? Description,
    Guid? ParentId,
    int SortOrder);
