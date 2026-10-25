using Convenciones.Api.Infrastructure;
using Convenciones.Identity;
using Convenciones.Publishing;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// The anonymous surface: published agreements and published news, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// These routes carry <c>AllowAnonymous()</c> **and** <c>ServesPublicData()</c>. The second is what makes
/// the request run on the <c>anon</c> database role: without it an anonymous request would run with the
/// login's privileges and read drafts. The handlers filter by id or paging only — never by publication
/// state — so the RLS policies are the guard and the leakage test is meaningful.
/// </para>
/// <para>
/// The projections are explicit record types, not serialised entities: the public surface is the only
/// unauthenticated one in the product, so every field here is a disclosure decision
/// (<c>security-model.md</c> §5.5, AC-S7, AC-S8).
/// </para>
/// </remarks>
public static class PublicEndpoints
{
    public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/public").WithTags("Public");

        group.MapGet("/agreements", ListAgreementsAsync)
            .AllowAnonymous()
            .ServesPublicData()
            .WithName("ListPublicAgreements");

        group.MapGet("/agreements/{id:guid}", GetAgreementAsync)
            .AllowAnonymous()
            .ServesPublicData()
            .WithName("GetPublicAgreement");

        group.MapGet("/news", ListNewsAsync)
            .AllowAnonymous()
            .ServesPublicData()
            .WithName("ListPublicNews");

        group.MapGet("/news/categories", ListNewsCategoriesAsync)
            .AllowAnonymous()
            .ServesPublicData()
            .WithName("ListPublicNewsCategories");

        group.MapGet("/news/{id:guid}", GetNewsAsync)
            .AllowAnonymous()
            .ServesPublicData()
            .WithName("GetPublicNews");

        return app;
    }

    private static async Task<IResult> ListAgreementsAsync(
        ConvencionesDbContext db,
        ListPublicAgreementsHandler handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = PublishingPaging.DefaultPageSize)
    {
        var list = await handler
            .HandleAsync(db.Agreements, db.Companies, page, pageSize, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(list);
    }

    private static async Task<IResult> GetAgreementAsync(
        Guid id,
        ConvencionesDbContext db,
        GetPublicAgreementHandler handler,
        CancellationToken cancellationToken)
    {
        var detail = await handler
            .HandleAsync(db.Agreements, db.Companies, id, cancellationToken)
            .ConfigureAwait(false);

        // 404, not 403: a draft agreement must be indistinguishable from one that does not exist (AC-S7).
        return detail is null
            ? NotFoundProblem($"No published agreement with id '{id}' exists.")
            : Results.Ok(detail);
    }

    private static async Task<IResult> ListNewsAsync(
        ConvencionesDbContext db,
        ListPublicNewsHandler handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = PublishingPaging.DefaultPageSize)
    {
        var list = await handler
            .HandleAsync(db.NewsArticles, db.NewsCategories, page, pageSize, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(list);
    }

    private static async Task<IResult> ListNewsCategoriesAsync(
        ConvencionesDbContext db,
        ListNewsCategoriesHandler handler,
        CancellationToken cancellationToken)
    {
        // Only the active categories are part of the portal's navigation.
        var categories = await handler
            .HandleAsync(db.NewsCategories, includeInactive: false, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(categories);
    }

    private static async Task<IResult> GetNewsAsync(
        Guid id,
        ConvencionesDbContext db,
        GetPublicNewsHandler handler,
        CancellationToken cancellationToken)
    {
        var detail = await handler
            .HandleAsync(db.NewsArticles, db.NewsCategories, id, cancellationToken)
            .ConfigureAwait(false);

        return detail is null
            ? NotFoundProblem($"No published news article with id '{id}' exists.")
            : Results.Ok(detail);
    }

    private static IResult NotFoundProblem(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });
}
