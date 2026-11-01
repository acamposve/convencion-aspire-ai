namespace Convenciones.Clauses;

/// <summary>Monthly limits for a company; a null limit means unlimited.</summary>
public sealed class CompanyQuota
{
    private CompanyQuota()
    {
    }

    public Guid CompanyId { get; private set; }

    public int? DocumentsLimit { get; private set; }

    public int? PagesLimit { get; private set; }

    public long? TokensLimit { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}

/// <summary>Consumption for a company in one month.</summary>
public sealed class ProcessingUsage
{
    private ProcessingUsage()
    {
    }

    public Guid CompanyId { get; private set; }

    public DateOnly Period { get; private set; }

    public int Documents { get; private set; }

    public int Pages { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public decimal CostEstimateUsd { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
