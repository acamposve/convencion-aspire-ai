namespace Convenciones.Catalog;

public sealed record CompanySummary(
    Guid Id,
    string Name,
    string? LegalName,
    string? TaxId,
    string? City,
    string? ContactEmail,
    bool IsArchived,
    uint RowVersion);

public sealed record CompanyDetail(
    Guid Id,
    string Name,
    string? LegalName,
    string? TaxId,
    string? LegacyCode,
    string? StreetAddress,
    string? City,
    Guid? CountryId,
    Guid? StateId,
    Guid? MunicipalityId,
    string? PostalCode,
    string? ContactName,
    string? ContactPosition,
    string? ContactPhone,
    string? ContactEmail,
    Guid? SectorId,
    Guid? TypeId,
    Guid? CategoryId,
    Guid? ActivityId,
    int? WorkerCount,
    int? EmployeeCount,
    string? SizeBand,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint RowVersion);

public sealed record CompanyListResponse(
    IReadOnlyList<CompanySummary> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record CreateCompanyRequest(
    string? Name,
    string? LegalName,
    string? TaxId,
    string? ContactEmail,
    string? City);

/// <summary>A partial update: a null field means "leave unchanged".</summary>
public sealed record UpdateCompanyRequest(
    string? Name,
    string? LegalName,
    string? TaxId,
    string? ContactEmail,
    string? City);

/// <summary>One option of a company catalog, as the comparison filters consume it (#138b).</summary>
public sealed record CatalogOption(Guid Id, string Name);

/// <summary>
/// The three catalogs a company can be classified with, in one response.
/// </summary>
/// <remarks>
/// Only active sectors and types are returned: a retired option must not be offered as a filter, and
/// the screen hides a selector whose list is empty. States have no active flag — they are geographic
/// reference rows, not a managed lookup.
/// </remarks>
public sealed record CompanyCatalogsResponse(
    IReadOnlyList<CatalogOption> Sectors,
    IReadOnlyList<CatalogOption> Types,
    IReadOnlyList<CatalogOption> States);
