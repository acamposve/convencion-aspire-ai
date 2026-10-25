using Convenciones.Api.Infrastructure;
using Convenciones.Agreements;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// Read-only access to the comparison heading taxonomy, needed to classify a clause.
/// </summary>
/// <remarks>
/// The comparison workspace itself is T9. This endpoint exists now because a clause cannot be
/// created without a heading (Q-3/A-16), and the client must be able to choose one without receiving
/// an invented list. It is read-only: the taxonomy is reference data curated by an administrator.
/// </remarks>
public static class ComparisonHeadingEndpoints
{
    public static IEndpointRouteBuilder MapComparisonHeadingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/comparison/headings", ListAsync)
            .RequiresCapability("comparison.read")
            .WithTags("Comparison")
            .WithName("ListComparisonHeadings");

        return app;
    }

    private static async Task<IResult> ListAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken,
        string? groupIds = null,
        bool includeExcluded = false)
    {
        if (accessor.Session is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "No session was resolved for this request.",
                extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Forbidden });
        }

        Guid[]? groups = null;

        if (!string.IsNullOrWhiteSpace(groupIds))
        {
            var parsed = new List<Guid>();

            foreach (var value in groupIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(value, out var groupId))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Validation failed",
                        detail: $"'{value}' is not a valid heading group id.",
                        extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ValidationFailed });
                }

                parsed.Add(groupId);
            }

            groups = parsed.ToArray();
        }

        var query = db.ComparisonHeadingGroups
            .AsNoTracking()
            .Where(group => group.IsActive);

        if (groups is { Length: > 0 })
        {
            query = query.Where(group => groups.Contains(group.Id));
        }

        var rows = await query
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Name)
            .Select(group => new
            {
                group.Id,
                group.Name,
                group.Description,
                group.RequiresEconomicComparison,
                Headings = group.Headings
                    .Where(heading => heading.IsActive && (includeExcluded || !heading.IsExcluded))
                    .OrderBy(heading => heading.SortOrder)
                    .ThenBy(heading => heading.Name)
                    .Select(heading => new HeadingResponse(
                        heading.Id,
                        heading.GroupId,
                        heading.Name,
                        heading.Description,
                        heading.IsExcluded,
                        heading.SortOrder))
                    .ToArray(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(rows
            .Select(group => new HeadingGroupResponse(
                group.Id,
                group.Name,
                group.Description,
                group.RequiresEconomicComparison,
                group.Headings))
            .ToArray());
    }
}

public sealed record HeadingGroupResponse(
    Guid Id,
    string Name,
    string? Description,
    bool RequiresEconomicComparison,
    IReadOnlyList<HeadingResponse> Headings);

public sealed record HeadingResponse(
    Guid Id,
    Guid GroupId,
    string Name,
    string? Description,
    bool IsExcluded,
    int SortOrder);
