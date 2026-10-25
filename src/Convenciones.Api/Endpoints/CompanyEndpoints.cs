using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// Company endpoints — the proving ground for tenant scoping.
/// </summary>
/// <remarks>
/// The first real business endpoints, chosen because the contract already specifies them
/// (<c>docs/architecture/api-contracts.md §4.2</c>) and because a company is the tenant boundary: if
/// scoping works here, it works anywhere the same helper is used.
/// </remarks>
public static class CompanyEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/companies").WithTags("Companies");

        group.MapGet("/", ListAsync)
            .RequiresCapability("company.read")
            .WithName("ListCompanies");

        group.MapGet("/{id:guid}", GetAsync)
            .RequiresCapability("company.read")
            .WithName("GetCompany");

        return app;
    }

    /// <summary>
    /// Lists the companies the caller may see.
    /// </summary>
    /// <remarks>
    /// The filter is applied in the query, not afterwards. Filtering in memory would read every
    /// company and then rely on the mapping to hide the rest — the same shape of bug as trusting a
    /// client-supplied identifier.
    /// </remarks>
    private static async Task<IResult> ListAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken,
        string? q = null,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Results.Problem(statusCode: 403, title: "Forbidden");
        }

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        var query = db.Companies.AsNoTracking();

        var visible = TenantScope.VisibleCompanyId(session);
        if (visible is { } only)
        {
            query = query.Where(company => company.Id == only);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(company =>
                EF.Functions.ILike(company.Name, term)
                || (company.LegalName != null && EF.Functions.ILike(company.LegalName, term)));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            .OrderBy(company => company.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(company => new CompanySummary(
                company.Id,
                company.Name,
                company.LegalName,
                company.TaxId,
                company.City,
                company.ContactEmail,
                company.ArchivedAt != null,
                company.RowVersion))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new CompanyListResponse(items, page, pageSize, total));
    }

    /// <summary>
    /// Returns one company, or 404 — never 403.
    /// </summary>
    /// <remarks>
    /// A 403 would confirm the company exists, which is itself a disclosure: it lets a caller map
    /// which companies the platform holds. "Not found" and "not yours" are deliberately
    /// indistinguishable from outside.
    /// </remarks>
    private static async Task<IResult> GetAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Results.Problem(statusCode: 403, title: "Forbidden");
        }

        if (!TenantScope.CanAccess(session, id))
        {
            return NotFound(id);
        }

        var company = await db.Companies
            .AsNoTracking()
            .Where(company => company.Id == id)
            .Select(company => new CompanyDetail(
                company.Id,
                company.Name,
                company.LegalName,
                company.TaxId,
                company.LegacyCode,
                company.StreetAddress,
                company.City,
                company.CountryId,
                company.StateId,
                company.MunicipalityId,
                company.PostalCode,
                company.ContactName,
                company.ContactPosition,
                company.ContactPhone,
                company.ContactEmail,
                company.SectorId,
                company.TypeId,
                company.CategoryId,
                company.ActivityId,
                company.WorkerCount,
                company.EmployeeCount,
                company.SizeBand,
                company.ArchivedAt != null,
                company.CreatedAt,
                company.UpdatedAt,
                company.RowVersion))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return company is null ? NotFound(id) : Results.Ok(company);
    }

    private static IResult NotFound(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No company with id '{id}' is visible to the caller.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });
}

public sealed record CompanyListResponse(
    IReadOnlyList<CompanySummary> Items,
    int Page,
    int PageSize,
    int Total);
