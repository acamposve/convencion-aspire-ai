using Convenciones.Catalog;

namespace Convenciones.Clauses;

/// <summary>One extracted clause, with its source location. Mirrors <c>platform.extracted_clause</c>.</summary>
public sealed class ExtractedClause : IAuditable
{
    private ExtractedClause()
    {
    }

    public Guid Id { get; private set; }

    public Guid RunId { get; private set; }

    public Guid DocumentId { get; private set; }

    public Guid CompanyId { get; private set; }

    public int Ordinal { get; private set; }

    public string? ClauseNumber { get; private set; }

    public string? Heading { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public int PageFrom { get; private set; }

    public int PageTo { get; private set; }

    public int? SourceCharStart { get; private set; }

    public int? SourceCharEnd { get; private set; }

    /// <summary>Serialised JSON array of source boxes; the database constrains it to jsonb.</summary>
    public string? SourceBoxes { get; private set; }

    public decimal? Confidence { get; private set; }

    public bool NeedsReview { get; private set; }

    public ClauseReviewStatus ReviewStatus { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string AuditModule => "documents";

    public string AuditEntityType => "extracted_clause";

    public string AuditEntityId => Id.ToString();

    public void Review(ClauseReviewStatus status, string? text, string? heading)
    {
        ReviewStatus = status;
        if (!string.IsNullOrWhiteSpace(text))
        {
            Text = text;
        }

        if (heading is not null)
        {
            Heading = heading;
        }
    }
}
