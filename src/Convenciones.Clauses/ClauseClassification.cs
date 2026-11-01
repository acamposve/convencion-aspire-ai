namespace Convenciones.Clauses;

/// <summary>One category assigned to a clause. Mirrors <c>platform.clause_classification</c>.</summary>
public sealed class ClauseClassification
{
    private ClauseClassification()
    {
    }

    public Guid Id { get; private set; }

    public Guid ClauseId { get; private set; }

    public Guid CategoryId { get; private set; }

    public Guid CompanyId { get; private set; }

    public decimal? Confidence { get; private set; }

    public bool IsPrimary { get; private set; }

    public string? Rationale { get; private set; }

    public string? Model { get; private set; }

    public string? PromptVersion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
