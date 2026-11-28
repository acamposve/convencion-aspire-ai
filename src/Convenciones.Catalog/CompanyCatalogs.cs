namespace Convenciones.Catalog;

/// <summary>
/// A read-only reference row a company points at: its sector (<c>app.company_sector</c>).
/// </summary>
/// <remarks>
/// The comparison workspace shows the sector of every published agreement (#138b), and the catalog is
/// managed directly in the database, so this is a projection, not an aggregate: no factory, no
/// invariants, no audit. The same shape covers <see cref="CompanyType"/> and <see cref="RegionState"/>.
/// </remarks>
public sealed class CompanySector
{
    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; }
}

/// <summary>A read-only reference row: the company type (<c>app.company_type</c>).</summary>
public sealed class CompanyType
{
    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; }
}

/// <summary>
/// A read-only reference row: the state or region of a company (<c>app.state</c>).
/// </summary>
/// <remarks>
/// Named <c>RegionState</c> rather than <c>State</c> so a reader never mistakes it for a workflow state
/// such as <c>PublicationState</c>. The table and the API field stay <c>state</c>.
/// </remarks>
public sealed class RegionState
{
    public Guid Id { get; private set; }

    public Guid CountryId { get; private set; }

    public string Name { get; private set; } = string.Empty;
}
