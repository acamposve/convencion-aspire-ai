namespace Convenciones.Catalog;

/// <summary>
/// A company: the tenant boundary of the whole product.
/// </summary>
/// <remarks>
/// Mapped column-by-column from <c>app.company</c> rather than by convention, because the schema
/// enforces geography consistency with composite foreign keys that a convention cannot express.
/// </remarks>
public sealed class Company : IAuditable
{
    private Company()
    {
    }

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

    public Guid? CreatedBy { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Row version (PostgreSQL <c>xmin</c>), used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    public string AuditModule => "catalog";

    public string AuditEntityType => "company";

    public string AuditEntityId => Id.ToString();

    public static Company Create(
        string name,
        string? legalName,
        string? taxId,
        string? contactEmail,
        string? city,
        Guid? actorId)
    {
        return new Company
        {
            Id = Guid.NewGuid(),
            Name = name,
            LegalName = legalName,
            TaxId = taxId,
            ContactEmail = contactEmail,
            City = city,
            CreatedBy = actorId,
            UpdatedBy = actorId,
        };
    }

    /// <summary>
    /// Applies a partial update. A null argument means "leave unchanged", which is what PATCH means.
    /// </summary>
    public void Apply(
        string? name,
        string? legalName,
        string? taxId,
        string? contactEmail,
        string? city,
        Guid? actorId)
    {
        if (name is not null)
        {
            Name = name;
        }

        if (legalName is not null)
        {
            LegalName = legalName;
        }

        if (taxId is not null)
        {
            TaxId = taxId;
        }

        if (contactEmail is not null)
        {
            ContactEmail = contactEmail;
        }

        if (city is not null)
        {
            City = city;
        }

        UpdatedBy = actorId;
    }
}
