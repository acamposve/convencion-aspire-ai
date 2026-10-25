using Convenciones.Api.Infrastructure;
using Convenciones.Identity;
using Convenciones.Legal;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// The legal library: statutes and their articles.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/architecture/api-contracts.md §4.5</c> and <c>docs/tasks/task-10-legal-library.md</c> are
/// the contract. Statutes are the only business data in the system that is <b>not</b> tenant-scoped:
/// they are company-independent reference data, and every active caller reads the same library.
/// Deliberately, no <c>TenantScope</c> call appears in this file — applying one by reflex would make
/// the library invisible to every user, and the risk is recorded in the task document.
/// </para>
/// <para>
/// Three database indexes are the guard for rules no single aggregate can evaluate. Their refusals
/// are translated here, and the 409 names the index, because a client should learn which rule it
/// broke:
/// </para>
/// <list type="bullet">
///   <item><c>statute_single_labour_law_key</c> — at most one <see cref="StatuteKind.LabourLaw"/>.</item>
///   <item><c>statute_name_key</c> — one statute per name, case-insensitively.</item>
///   <item><c>statute_article_number_key</c> — one article per number within a statute.</item>
/// </list>
/// </remarks>
public static class StatuteEndpoints
{
    internal const string SingleLabourLawIndex = "statute_single_labour_law_key";

    internal const string StatuteNameIndex = "statute_name_key";

    internal const string ArticleNumberIndex = "statute_article_number_key";

    public static IEndpointRouteBuilder MapStatuteEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/statutes").WithTags("Statutes");

        group.MapGet("/", ListAsync)
            .RequiresCapability("statute.read")
            .WithName("ListStatutes");

        group.MapPost("/", CreateAsync)
            .RequiresCapability("statute.write")
            .WithName("CreateStatute");

        group.MapGet("/{id:guid}", GetAsync)
            .RequiresCapability("statute.read")
            .WithName("GetStatute");

        group.MapPatch("/{id:guid}", UpdateAsync)
            .RequiresCapability("statute.write")
            .WithName("UpdateStatute");

        group.MapGet("/{id:guid}/articles", ListArticlesAsync)
            .RequiresCapability("statute.read")
            .WithName("ListStatuteArticles");

        group.MapPost("/{id:guid}/articles", AddArticleAsync)
            .RequiresCapability("statute.write")
            .WithName("AddStatuteArticle");

        group.MapGet("/{id:guid}/articles/{articleId:guid}", GetArticleAsync)
            .RequiresCapability("statute.read")
            .WithName("GetStatuteArticle");

        group.MapPatch("/{id:guid}/articles/{articleId:guid}", UpdateArticleAsync)
            .RequiresCapability("statute.write")
            .WithName("UpdateStatuteArticle");

        group.MapDelete("/{id:guid}/articles/{articleId:guid}", DeleteArticleAsync)
            .RequiresCapability("statute.write")
            .WithName("DeleteStatuteArticle");

        return app;
    }

    // ---- Statutes -------------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken,
        string? kind = null,
        string? q = null,
        int page = 1,
        int pageSize = StatuteQueries.DefaultPageSize)
    {
        if (accessor.Session is null)
        {
            return Forbidden();
        }

        StatuteKind? kindFilter = null;

        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!StatuteKinds.TryParse(kind, out var parsed))
            {
                return Validation("Kind must be 'LabourLaw' or 'OtherStatute'.");
            }

            kindFilter = parsed;
        }

        // No company predicate. Statutes are reference data shared by every tenant.
        var list = await StatuteQueries
            .ListAsync(db.Statutes, kindFilter, q, page, pageSize, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(list);
    }

    private static async Task<IResult> CreateAsync(
        CreateStatuteRequest request,
        CreateStatuteHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        StatuteCommandResult result;

        try
        {
            result = await handler
                .HandleAsync(request, session.UserId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return FromUniqueViolation(exception);
        }

        return Map(result, $"/statutes/{result.Statute?.Id}");
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        if (accessor.Session is null)
        {
            return Forbidden();
        }

        var detail = await StatuteQueries
            .DetailAsync(db.Statutes, id, cancellationToken)
            .ConfigureAwait(false);

        return detail is null ? NotFoundStatute(id) : Results.Ok(detail);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateStatuteRequest request,
        HttpRequest httpRequest,
        UpdateStatuteHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (!TryReadIfMatch(httpRequest, out var expectedVersion))
        {
            return Validation(
                "The If-Match header with the current rowVersion is required for a partial update.");
        }

        StatuteCommandResult result;

        try
        {
            result = await handler
                .HandleAsync(id, request, expectedVersion, session.UserId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification(null);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return FromUniqueViolation(exception);
        }

        return Map(result);
    }

    // ---- Articles -------------------------------------------------------------------------------

    private static async Task<IResult> ListArticlesAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        if (accessor.Session is null)
        {
            return Forbidden();
        }

        var articles = await StatuteQueries
            .ArticlesAsync(db.Statutes, id, cancellationToken)
            .ConfigureAwait(false);

        return articles is null ? NotFoundStatute(id) : Results.Ok(articles);
    }

    private static async Task<IResult> AddArticleAsync(
        Guid id,
        StatuteArticleRequest request,
        AddStatuteArticleHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        StatuteCommandResult result;

        try
        {
            result = await handler
                .HandleAsync(id, request, session.UserId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return FromUniqueViolation(exception, request.Number);
        }

        return Map(result, $"/statutes/{id}/articles/{result.Article?.Id}");
    }

    private static async Task<IResult> GetArticleAsync(
        Guid id,
        Guid articleId,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        if (accessor.Session is null)
        {
            return Forbidden();
        }

        var article = await StatuteQueries
            .ArticleAsync(db.Statutes, id, articleId, cancellationToken)
            .ConfigureAwait(false);

        return article is null ? NotFoundArticle(articleId) : Results.Ok(article);
    }

    private static async Task<IResult> UpdateArticleAsync(
        Guid id,
        Guid articleId,
        StatuteArticleRequest request,
        HttpRequest httpRequest,
        UpdateStatuteArticleHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (!TryReadIfMatch(httpRequest, out var expectedVersion))
        {
            return Validation(
                "The If-Match header with the current rowVersion is required for a partial update.");
        }

        StatuteCommandResult result;

        try
        {
            result = await handler
                .HandleAsync(
                    id,
                    articleId,
                    request,
                    expectedVersion,
                    session.UserId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification(null);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return FromUniqueViolation(exception, request.Number);
        }

        return Map(result);
    }

    private static async Task<IResult> DeleteArticleAsync(
        Guid id,
        Guid articleId,
        HttpRequest httpRequest,
        DeleteStatuteArticleHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        if (accessor.Session is null)
        {
            return Forbidden();
        }

        if (!TryReadIfMatch(httpRequest, out var expectedVersion))
        {
            return Validation("The If-Match header with the current rowVersion is required.");
        }

        StatuteCommandResult result;

        try
        {
            result = await handler
                .HandleAsync(id, articleId, expectedVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification(null);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return FromUniqueViolation(exception);
        }

        return Map(result);
    }

    // ---- Translation ----------------------------------------------------------------------------

    private static IResult Map(StatuteCommandResult result, string? createdLocation = null) =>
        result.Status switch
        {
            StatuteCommandStatus.Created when result.Statute is not null =>
                Results.Created(new Uri(createdLocation!, UriKind.Relative), result.Statute),
            StatuteCommandStatus.Created =>
                Results.Created(new Uri(createdLocation!, UriKind.Relative), result.Article),
            StatuteCommandStatus.Ok when result.Statute is not null => Results.Ok(result.Statute),
            StatuteCommandStatus.Ok => Results.Ok(result.Article),
            StatuteCommandStatus.NoContent => Results.NoContent(),
            StatuteCommandStatus.NotFound => NotFoundProblem(result.Detail),
            StatuteCommandStatus.ValidationFailed => Validation(
                result.Detail ?? "The request is invalid."),
            StatuteCommandStatus.Conflict => Conflict(
                result.Detail ?? "The request conflicts with existing data."),
            StatuteCommandStatus.ConcurrentModification => ConcurrentModification(result.Detail),
            _ => InvalidState(result.Detail ?? "The operation is not allowed in the current state."),
        };

    /// <summary>
    /// Turns a unique-index refusal into the 409 the contract defines, naming the index.
    /// </summary>
    /// <remarks>
    /// The index is the authority for these rules: the single-labour-law rule spans rows, the statute
    /// name is unique across the library, and the article number is unique within a statute. Naming
    /// the index tells the caller which rule refused it, which is more useful than "duplicate".
    /// </remarks>
    private static IResult FromUniqueViolation(DbUpdateException exception, int? articleNumber = null)
    {
        var constraint = (exception.InnerException as PostgresException)?.ConstraintName;

        return constraint switch
        {
            SingleLabourLawIndex => Conflict(
                "Only one LabourLaw statute may exist; the partial unique index "
                + $"{SingleLabourLawIndex} refused the second one."),
            StatuteNameIndex => Conflict(
                "A statute with that name already exists; the unique index "
                + $"{StatuteNameIndex} refuses a duplicate name."),
            ArticleNumberIndex => Conflict(
                articleNumber is { } number
                    ? $"Article number {number} already exists in this statute "
                      + $"({ArticleNumberIndex})."
                    : $"That article number already exists in this statute ({ArticleNumberIndex})."),
            _ => Conflict($"A unique index refused the write: {constraint}."),
        };
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };

    private static bool TryReadIfMatch(HttpRequest request, out uint version)
    {
        version = 0;
        var header = request.Headers.IfMatch.ToString();

        return !string.IsNullOrWhiteSpace(header)
               && uint.TryParse(header.Trim().Trim('"'), out version);
    }

    private static IResult Forbidden() =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: "No session was resolved for this request.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Forbidden });

    private static IResult Validation(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Validation failed",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ValidationFailed });

    private static IResult Conflict(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Conflict });

    private static IResult InvalidState(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Invalid state",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.InvalidState });

    private static IResult ConcurrentModification(string? detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail
                ?? "The resource was modified by someone else. Re-read it and retry with the "
                + "current rowVersion.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ConcurrentModification });

    private static IResult NotFoundProblem(string? detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: detail ?? "The resource does not exist.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult NotFoundStatute(Guid id) =>
        NotFoundProblem($"No statute with id '{id}' exists.");

    private static IResult NotFoundArticle(Guid id) =>
        NotFoundProblem($"No statute article with id '{id}' exists.");
}
