using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// Company endpoints — the proving ground for tenant scoping and for the write path.
/// </summary>
/// <remarks>
/// Chosen because the contract already specifies them (<c>docs/architecture/api-contracts.md §4.2</c>)
/// and because a company is the tenant boundary: if scoping works here, it works wherever the same
/// helper is used.
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

        // The comparison filters need the options as data, not as bare ids (#138b). Registered before
        // the by-id route for readability; the `:guid` constraint would not match "catalogs" anyway.
        group.MapGet("/catalogs", ListCatalogsAsync)
            .RequiresCapability("company.read")
            .WithName("ListCompanyCatalogs");

        group.MapGet("/{id:guid}", GetAsync)
            .RequiresCapability("company.read")
            .WithName("GetCompany");

        // Creation needs BOTH capabilities. A user who could create a company without cross-company
        // visibility would be granting themselves a new tenant — escalation through a side door.
        group.MapPost("/", CreateAsync)
            .RequiresCapability("company.write", "company.read_all")
            .WithName("CreateCompany");

        group.MapPatch("/{id:guid}", PatchAsync)
            .RequiresCapability("company.write")
            .WithName("UpdateCompany");

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
    /// <summary>
    /// The sector, type and state catalogs a company can be classified with (#138b).
    /// </summary>
    /// <remarks>
    /// One request instead of three: the comparison screen needs all of them at once to draw its
    /// filters, and a third round trip would only add latency. Only active sectors and types are
    /// returned — a retired option must not be offered — and the screen hides a selector whose list
    /// comes back empty. States carry no active flag: they are geographic reference rows.
    /// </remarks>
    private static async Task<IResult> ListCatalogsAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        if (accessor.Session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var sectors = await db.CompanySectors
            .AsNoTracking()
            .Where(sector => sector.IsActive)
            .OrderBy(sector => sector.SortOrder)
            .ThenBy(sector => sector.Name)
            .Select(sector => new CatalogOption(sector.Id, sector.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var types = await db.CompanyTypes
            .AsNoTracking()
            .Where(type => type.IsActive)
            .OrderBy(type => type.SortOrder)
            .ThenBy(type => type.Name)
            .Select(type => new CatalogOption(type.Id, type.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var states = await db.RegionStates
            .AsNoTracking()
            .OrderBy(state => state.Name)
            .Select(state => new CatalogOption(state.Id, state.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new CompanyCatalogsResponse(sectors, types, states));
    }

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
            return Forbidden("No session was resolved for this request.");
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
            return Forbidden("No session was resolved for this request.");
        }

        if (!TenantScope.CanAccess(session, id))
        {
            return NotFound(id);
        }

        var company = await db.Companies
            .AsNoTracking()
            .Where(company => company.Id == id)
            .Select(company => ToDetail(company))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return company is null ? NotFound(id) : Results.Ok(company);
    }

    /// <summary>
    /// Creates a company.
    /// </summary>
    /// <remarks>
    /// The creator is recorded from the session, never from the body. Creating a company does not
    /// attach it to the creator: <c>user_profile.company_id</c> is unchanged, so a new tenant does not
    /// silently become "mine" and the caller's visibility is exactly what it was.
    /// </remarks>
    private static async Task<IResult> CreateAsync(
        CreateCompanyRequest request,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var validation = Validate(request.Name, request.ContactEmail);
        if (validation is not null)
        {
            return validation;
        }

        var company = Company.Create(
            request.Name!.Trim(),
            request.LegalName?.Trim(),
            request.TaxId?.Trim(),
            request.ContactEmail?.Trim(),
            request.City?.Trim(),
            session.UserId);

        db.Companies.Add(company);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return Conflict("A company with the same tax id already exists.");
        }

        return Results.Created(
            new Uri($"/companies/{company.Id}", UriKind.Relative),
            ToDetail(company));
    }

    /// <summary>
    /// Applies a partial update, guarded by <c>If-Match</c>.
    /// </summary>
    /// <remarks>
    /// A missing <c>If-Match</c> is a 400 rather than a silent last-write-wins: the legacy system had
    /// no concurrency control and silently lost one of two concurrent edits, and accepting an
    /// unconditional write here would reintroduce exactly that.
    /// </remarks>
    private static async Task<IResult> PatchAsync(
        Guid id,
        UpdateCompanyRequest request,
        HttpRequest httpRequest,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        if (!TenantScope.CanAccess(session, id))
        {
            return NotFound(id);
        }

        var ifMatch = httpRequest.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch) || !TryParseRowVersion(ifMatch, out var expectedVersion))
        {
            return Validation(
                "The If-Match header with the current rowVersion is required for a partial update.");
        }

        var company = await db.Companies
            .Where(company => company.Id == id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (company is null)
        {
            return NotFound(id);
        }

        var validation = Validate(request.Name, request.ContactEmail);
        if (validation is not null)
        {
            return validation;
        }

        db.Entry(company).Property(entity => entity.RowVersion).OriginalValue = expectedVersion;

        company.Apply(
            request.Name?.Trim(),
            request.LegalName?.Trim(),
            request.TaxId?.Trim(),
            request.ContactEmail?.Trim(),
            request.City?.Trim(),
            session.UserId);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: "The company was modified by someone else. Re-read it and retry with the current rowVersion.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = ErrorCodes.ConcurrentModification,
                });
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return Conflict("A company with the same tax id already exists.");
        }

        return Results.Ok(ToDetail(company));
    }

    private static CompanyDetail ToDetail(Company company) =>
        new(
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
            company.RowVersion);

    private static bool TryParseRowVersion(string header, out uint version)
    {
        var value = header.Trim().Trim('"');
        return uint.TryParse(value, out version);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static IResult? Validate(string? name, string? contactEmail)
    {
        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            return Validation("Name cannot be blank.");
        }

        if (contactEmail is not null
            && !string.IsNullOrWhiteSpace(contactEmail)
            && !contactEmail.Contains('@', StringComparison.Ordinal))
        {
            return Validation("Contact email is not a valid address.");
        }

        return null;
    }

    private static IResult Validation(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Validation failed",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ValidationFailed });

    private static IResult Forbidden(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Forbidden });

    private static IResult Conflict(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Conflict });

    private static IResult NotFound(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No company with id '{id}' is visible to the caller.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });
}
