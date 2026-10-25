namespace Convenciones.Catalog;

/// <summary>
/// A company: the tenant boundary of the whole product.
/// </summary>
/// <remarks>
/// Mapped column-by-column from <c>app.company</c> rather than by convention, because the schema
/// enforces geography consistency with composite foreign keys that a convention cannot express.
/// </remarks>
public sealed class Company
{
    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? LegalName { get; private set; }

    public string? TaxId { get; private set; }

    public string? LegacyCode { get; private set; }

    public string? StreetAddress { get; private set; }

    public string? City { get; private set; }

    public Guid? CountryId { get; private set; }

    public Guid? StateId { get; private set; }

    public Guid? MunicipalityId { get; private set; }

    public string? PostalCode { get; private set; }

    public string? ContactName { get; private set; }

    public string? ContactPosition { get; private set; }

    public string? ContactPhone { get; private set; }

    public string? ContactEmail { get; private set; }

    public Guid? SectorId { get; private set; }

    public Guid? TypeId { get; private set; }

    public Guid? CategoryId { get; private set; }

    public Guid? ActivityId { get; private set; }

    public int? WorkerCount { get; private set; }

    public int? EmployeeCount { get; private set; }

    public string? SizeBand { get; private set; }

    public Guid? LogoDocumentId { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Row version, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    internal void Rename(string name) => Name = name;
}
