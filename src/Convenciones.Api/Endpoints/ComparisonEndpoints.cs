using Convenciones.Agreements;
using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Convenciones.Comparison;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// The comparison workspace: candidates for a heading, and the adjacency projection across the
/// selected agreements.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/architecture/api-contracts.md §4.4</c> and <c>docs/tasks/task-09-comparison.md</c> are the
/// contract. This is the product's read surface: every query is <c>AsNoTracking</c>, the routes are
/// GET-only, and there is no write method in the comparison module (T9 AC 4).
/// </para>
/// <para>
/// **Adjacency, not alignment (Q-6).** Clauses are grouped by heading and shown side by side inside
/// their agreement. The response never pairs clause A with clause B; the UI must not invent that
/// pairing (R2-01 is the alignment feature).
/// </para>
/// </remarks>
public static class ComparisonEndpoints
{
    private const int DefaultCandidatePageSize = 10;
    private const int MaxCandidatePageSize = 50;
    private const int DefaultComparisonPageSize = 5;
    private const int MaxComparisonPageSize = 10;
    private const int MaxRequestedAgreements = 50;

    public static IEndpointRouteBuilder MapComparisonEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/comparison").WithTags("Comparison");

        group.MapGet("/heading-groups", ListHeadingGroupsAsync)
            .RequiresCapability("comparison.read")
            .WithName("ListComparisonHeadingGroups");

        group.MapGet("/candidates", ListCandidatesAsync)
            .RequiresCapability("comparison.read")
            .WithName("ListComparisonCandidates");

        group.MapGet("/", CompareAsync)
            .RequiresCapability("comparison.read")
            .WithName("GetComparison");

        return app;
    }

    private static async Task<IResult> ListHeadingGroupsAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        if (accessor.Session is null)
        {
            return Forbidden();
        }

        var groups = await db.ComparisonHeadingGroups
            .AsNoTracking()
            .Where(group => group.IsActive)
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Name)
            .Select(group => new HeadingGroupView(
                group.Id,
                group.Name,
                group.Description,
                group.RequiresEconomicComparison,
                group.SortOrder))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(groups);
    }

    private static async Task<IResult> ListCandidatesAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken,
        Guid headingId,
        Guid? companyId = null,
        int page = 1,
        int pageSize = DefaultCandidatePageSize)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (headingId == Guid.Empty)
        {
            return Validation("A headingId is required.");
        }

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxCandidatePageSize ? DefaultCandidatePageSize : pageSize;

        var heading = await LoadHeadingAsync(db, headingId, cancellationToken).ConfigureAwait(false);
        if (heading is null)
        {
            return NotFoundHeading(headingId);
        }

        var targetCompany = companyId ?? session.CompanyId;
        if (targetCompany is not { } company || !TenantScope.CanAccess(session, company))
        {
            return NotFoundCompany(companyId ?? Guid.Empty);
        }

        // An excluded heading is not offered in the workspace. Returning an empty page is explicit:
        // the caller learns that there is nothing to compare, rather than receiving "No Compara"
        // clauses that would pollute a real comparison.
        if (heading.IsExcluded)
        {
            return Results.Ok(new ComparisonCandidatePage([], page, pageSize, 0));
        }

        var query = ComparisonQueries.Candidates(
            db.Agreements.AsNoTracking(),
            db.Companies.AsNoTracking(),
            company,
            headingId);

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(row => new ComparisonCandidate(
                row.AgreementId,
                row.CompanyId,
                row.CompanyName,
                AgreementTitles.Compose(row.CompanyName, row.ValidFrom, row.ValidTo),
                row.ValidFrom,
                row.ValidTo,
                row.ClauseCount))
            .ToArray();

        return Results.Ok(new ComparisonCandidatePage(items, page, pageSize, total));
    }

    private static async Task<IResult> CompareAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken,
        Guid headingId,
        string? agreementIds = null,
        int page = 1,
        int pageSize = DefaultComparisonPageSize)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        if (headingId == Guid.Empty)
        {
            return Validation("A headingId is required.");
        }

        if (!TryParseAgreementIds(agreementIds, out var requestedIds, out var idError))
        {
            return Validation(idError!);
        }

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxComparisonPageSize
            ? DefaultComparisonPageSize
            : pageSize;

        var heading = await LoadHeadingAsync(db, headingId, cancellationToken).ConfigureAwait(false);
        if (heading is null)
        {
            return NotFoundHeading(headingId);
        }

        var agreements = await db.Agreements
            .AsNoTracking()
            .Include(agreement => agreement.Clauses)
            .Where(agreement => requestedIds.Contains(agreement.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (agreements.Count != requestedIds.Length)
        {
            // Include inaccessible ids in the count check: another company's agreement is
            // indistinguishable from a missing one, and a 404 does not confirm existence.
            return NotFoundAgreements();
        }

        if (agreements.Any(agreement => !TenantScope.CanAccess(session, agreement.CompanyId)))
        {
            return NotFoundAgreements();
        }

        var lookup = agreements.ToDictionary(agreement => agreement.Id);
        var ordered = requestedIds.Select(id => lookup[id]).ToArray();

        // A draft is never rendered, even when the caller can read it. The contract puts it in
        // unpublishedRequested instead of silently dropping it, so the caller learns why a column is
        // missing. It is not part of the paged published set.
        var unpublishedRequested = ordered
            .Where(agreement => agreement.State is PublicationState.Draft)
            .Select(agreement => agreement.Id)
            .ToArray();

        var published = ordered
            .Where(agreement => agreement.State is PublicationState.Published)
            .OrderByDescending(agreement => agreement.ValidFrom)
            .ThenBy(agreement => agreement.Id)
            .ToArray();

        // No heading-level comparison exists for an excluded heading, regardless of what was
        // requested. The flag in the response says why.
        if (heading.IsExcluded)
        {
            published = [];
        }

        var total = published.Length;
        var pageOfAgreements = published
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        var companyNames = await db.Companies
            .AsNoTracking()
            .Where(company => agreements.Select(agreement => agreement.CompanyId).Contains(company.Id))
            .ToDictionaryAsync(company => company.Id, company => company.Name, cancellationToken)
            .ConfigureAwait(false);

        var views = pageOfAgreements
            .Select(agreement => new ComparisonAgreementView(
                agreement.Id,
                agreement.CompanyId,
                companyNames[agreement.CompanyId],
                agreement.ComposeTitle(companyNames[agreement.CompanyId]),
                agreement.ValidFrom,
                agreement.ValidTo,
                agreement.Clauses
                    .Where(clause => clause.HeadingId == headingId)
                    .OrderBy(clause => clause.Number)
                    .Select(clause => new ComparisonClauseView(
                        clause.Id,
                        clause.Number,
                        clause.Title,
                        clause.Summary,
                        clause.Body,
                        clause.EconomicComparisonNote,
                        clause.IsExcludedFromCompilation))
                    .ToArray()))
            .ToArray();

        return Results.Ok(new ComparisonView(
            heading,
            [],
            views,
            unpublishedRequested,
            page,
            pageSize,
            total));
    }

    private static async Task<ComparisonHeadingView?> LoadHeadingAsync(
        ConvencionesDbContext db,
        Guid headingId,
        CancellationToken cancellationToken) =>
        await db.ComparisonHeadings
            .AsNoTracking()
            .Where(heading => heading.Id == headingId && heading.IsActive && heading.Group!.IsActive)
            .Select(heading => new ComparisonHeadingView(
                heading.Id,
                heading.GroupId,
                heading.Group!.Name,
                heading.Name,
                heading.IsExcluded,
                heading.Group.RequiresEconomicComparison,
                heading.SortOrder))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static bool TryParseAgreementIds(
        string? raw,
        out Guid[] ids,
        out string? error)
    {
        ids = [];
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "At least one agreementId is required.";
            return false;
        }

        var parsed = new List<Guid>();

        foreach (var value in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(value, out var id))
            {
                error = $"'{value}' is not a valid agreement id.";
                return false;
            }

            parsed.Add(id);
        }

        if (parsed.Count == 0)
        {
            error = "At least one agreementId is required.";
            return false;
        }

        if (parsed.Distinct().Count() > MaxRequestedAgreements)
        {
            error = $"At most {MaxRequestedAgreements} agreements can be compared at once.";
            return false;
        }

        ids = parsed.Distinct().ToArray();
        return true;
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

    private static IResult NotFoundHeading(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No active heading with id '{id}' exists.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult NotFoundCompany(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No company with id '{id}' is visible to the caller.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult NotFoundAgreements() =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: "One or more agreements are not visible to the caller.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });
}
