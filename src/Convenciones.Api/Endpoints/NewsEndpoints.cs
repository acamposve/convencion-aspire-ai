using Convenciones.Agreements;
using Convenciones.Api.Infrastructure;
using Convenciones.Identity;
using Convenciones.Publishing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// News administration: create, edit, publish and unpublish. Publishing is its own capability
/// (<c>news.publish</c>), separate from writing the article, and the database trigger enforces it again.
/// </summary>
public static class NewsEndpoints
{
    public static IEndpointRouteBuilder MapNewsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/news").WithTags("News");

        group.MapGet("/", ListAsync)
            .RequiresCapability("news.read")
            .WithName("ListNews");

        group.MapGet("/categories", ListCategoriesAsync)
            .RequiresCapability("news.read")
            .WithName("ListNewsCategories");

        group.MapPost("/categories", CreateCategoryAsync)
            .RequiresCapability("news.write")
            .WithName("CreateNewsCategory");

        group.MapGet("/{id:guid}", GetAsync)
            .RequiresCapability("news.read")
            .WithName("GetNews");

        group.MapPost("/", CreateAsync)
            .RequiresCapability("news.write")
            .WithName("CreateNews");

        group.MapPatch("/{id:guid}", UpdateAsync)
            .RequiresCapability("news.write")
            .WithName("UpdateNews");

        group.MapPost("/{id:guid}/publish", PublishAsync)
            .RequiresCapability("news.publish")
            .WithName("PublishNews");

        group.MapPost("/{id:guid}/unpublish", UnpublishAsync)
            .RequiresCapability("news.publish")
            .WithName("UnpublishNews");

        return app;
    }

    private static async Task<IResult> ListAsync(
        ConvencionesDbContext db,
        ListNewsHandler handler,
        CancellationToken cancellationToken,
        string? state = null,
        Guid? categoryId = null,
        int page = 1,
        int pageSize = PublishingPaging.DefaultPageSize)
    {
        PublicationState? stateFilter = null;

        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!TryParseState(state, out var parsed))
            {
                return Validation("State must be 'Draft' or 'Published'.");
            }

            stateFilter = parsed;
        }

        var list = await handler
            .HandleAsync(
                db.NewsArticles,
                db.NewsCategories,
                new ListNewsQuery(stateFilter, categoryId, page, pageSize),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(list);
    }

    private static async Task<IResult> ListCategoriesAsync(
        ConvencionesDbContext db,
        ListNewsCategoriesHandler handler,
        CancellationToken cancellationToken)
    {
        var categories = await handler
            .HandleAsync(db.NewsCategories, includeInactive: true, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(categories);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ConvencionesDbContext db,
        GetNewsHandler handler,
        CancellationToken cancellationToken)
    {
        var detail = await handler
            .HandleAsync(db.NewsArticles, db.NewsCategories, id, cancellationToken)
            .ConfigureAwait(false);

        return detail is null ? NotFoundArticle(id) : Results.Ok(detail);
    }

    private static Task<IResult> CreateAsync(
        CreateNewsRequest request,
        CreateNewsHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(request, session.UserId, cancellationToken),
                "/news"));

    private static Task<IResult> UpdateAsync(
        Guid id,
        UpdateNewsRequest request,
        UpdateNewsHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(id, request, session.UserId, cancellationToken)));

    private static Task<IResult> PublishAsync(
        Guid id,
        PublishNewsHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(id, session.UserId, cancellationToken)));

    private static Task<IResult> UnpublishAsync(
        Guid id,
        UnpublishNewsHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(id, session.UserId, cancellationToken)));

    private static Task<IResult> CreateCategoryAsync(
        CreateNewsCategoryRequest request,
        CreateNewsCategoryHandler handler,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => handler.HandleAsync(request, cancellationToken),
            "/news/categories");

    // ---- Translation -----------------------------------------------------------------------------

    private static Task<IResult> WithSession(
        ISessionAccessor accessor,
        Func<SessionContext, Task<IResult>> action) =>
        accessor.Session is { } session ? action(session) : Task.FromResult(Forbidden());

    private static async Task<IResult> ExecuteAsync(
        Func<Task<NewsCommandResult>> action,
        string? location = null)
    {
        try
        {
            return Map(await action().ConfigureAwait(false), location);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification(
                "The article was modified by someone else. Re-read it and retry.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return ConflictFromDatabase(exception);
        }
        catch (DbUpdateException exception) when (IsCapabilityViolation(exception))
        {
            return ForbiddenByDatabase(exception);
        }
    }

    private static IResult Map(NewsCommandResult result, string? location)
    {
        var uri = new Uri(location ?? "/news", UriKind.Relative);

        return result.Status switch
        {
            NewsCommandStatus.Created when result.Article is not null => Results.Created(uri, result.Article),
            NewsCommandStatus.Created when result.Category is not null => Results.Created(uri, result.Category),
            NewsCommandStatus.Ok when result.Article is not null => Results.Ok(result.Article),
            NewsCommandStatus.NotFound => NotFoundProblem(
                result.Detail ?? "The resource does not exist."),
            NewsCommandStatus.ValidationFailed => Validation(
                result.Detail ?? "The request is invalid."),
            NewsCommandStatus.Conflict => Conflict(
                result.Detail ?? "The request conflicts with the current state."),
            _ => ConcurrentModification(result.Detail),
        };
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };

    private static bool IsCapabilityViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "42501" };

    private static IResult ConflictFromDatabase(DbUpdateException exception)
    {
        var constraint = (exception.InnerException as PostgresException)?.ConstraintName;

        return constraint switch
        {
            "news_category_name_key" => Conflict("A category with that name already exists."),
            _ => Conflict($"A unique index refused the write: {constraint}."),
        };
    }

    private static IResult ForbiddenByDatabase(DbUpdateException exception) =>
        Forbidden(
            (exception.InnerException as PostgresException)?.MessageText
            ?? "The capability 'news.publish' is required.");

    private static bool TryParseState(string value, out PublicationState state)
    {
        state = default;

        return value.Trim().ToLowerInvariant() switch
        {
            "draft" => Set(PublicationState.Draft, out state),
            "published" => Set(PublicationState.Published, out state),
            _ => false,
        };

        static bool Set(PublicationState value, out PublicationState target)
        {
            target = value;
            return true;
        }
    }

    private static IResult Forbidden(string? detail = null) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: detail ?? "No session was resolved for this request.",
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

    private static IResult ConcurrentModification(string? detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail ?? "The resource was modified by someone else. Re-read it and retry.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ConcurrentModification });

    private static IResult NotFoundProblem(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult NotFoundArticle(Guid id) =>
        NotFoundProblem($"No news article with id '{id}' exists.");
}
