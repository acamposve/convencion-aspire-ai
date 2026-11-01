using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Convenciones.Identity;

namespace Convenciones.Api.Endpoints;

public static class QuotaEndpoints
{
    public static IEndpointRouteBuilder MapQuotaEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/companies/{id:guid}/usage", GetUsageAsync)
            .RequiresCapability("clause.read")
            .WithTags("Quotas")
            .WithName("GetCompanyUsage");

        app.MapGet("/companies/{id:guid}/quota", GetQuotaAsync)
            .RequiresCapability("quota.manage")
            .WithTags("Quotas")
            .WithName("GetCompanyQuota");

        app.MapPut("/companies/{id:guid}/quota", SetQuotaAsync)
            .RequiresCapability("quota.manage")
            .WithTags("Quotas")
            .WithName("SetCompanyQuota");

        return app;
    }

    private static async Task<IResult> GetUsageAsync(
        Guid id,
        ISessionAccessor accessor,
        ProcessingQuotaService quotas,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (!TenantScope.CanAccess(session, id))
        {
            return NotFound(id);
        }

        return Results.Ok(await quotas.GetAsync(id, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetQuotaAsync(
        Guid id,
        ISessionAccessor accessor,
        ProcessingQuotaService quotas,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (!TenantScope.CanAccess(session, id))
        {
            return NotFound(id);
        }

        return Results.Ok(await quotas.GetAsync(id, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> SetQuotaAsync(
        Guid id,
        QuotaLimitsRequest request,
        ISessionAccessor accessor,
        ProcessingQuotaService quotas,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (!TenantScope.CanAccess(session, id))
        {
            return NotFound(id);
        }

        if (request.Documents is < 0 || request.Pages is < 0 || request.Tokens is < 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Validation failed",
                detail: "Quota limits cannot be negative.",
                extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" });
        }

        await quotas.SetAsync(
            id,
            new QuotaLimits(request.Documents, request.Pages, request.Tokens),
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(await quotas.GetAsync(id, cancellationToken).ConfigureAwait(false));
    }

    private static IResult Forbidden() =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: "No session was resolved for this request.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Forbidden });

    private static IResult NotFound(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No company with id '{id}' is visible to the caller.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });
}

public sealed record QuotaLimitsRequest(int? Documents, int? Pages, long? Tokens);
