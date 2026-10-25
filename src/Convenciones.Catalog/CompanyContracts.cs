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

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize);
