namespace Convenciones.Agreements;

/// <summary>A block of the comparison taxonomy. Read-only in T8; T9 owns the comparison workspace.</summary>
public sealed class ComparisonHeadingGroup
{
    private ComparisonHeadingGroup()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool RequiresEconomicComparison { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; }

    public List<ComparisonHeading> Headings { get; private set; } = [];
}

/// <summary>
/// A heading a clause is classified under. Every clause must reference one (Q-3/A-16).
/// </summary>
public sealed class ComparisonHeading
{
    private ComparisonHeading()
    {
    }

    public Guid Id { get; private set; }

    public Guid GroupId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>True means its clauses are not offered in the comparison workspace.</summary>
    public bool IsExcluded { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; }

    public ComparisonHeadingGroup? Group { get; private set; }
}
