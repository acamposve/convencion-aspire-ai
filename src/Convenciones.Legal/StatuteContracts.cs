using Convenciones.Catalog;

namespace Convenciones.Legal;

using System.Text.Json.Serialization;

/// <summary>Parsing for the statute kind, used by the body of <c>POST /statutes</c> and the list filter.</summary>
/// <remarks>
/// Parsing is explicit rather than <c>Enum.TryParse</c> alone, because that accepts a numeric string:
/// <c>"1"</c> would silently become <see cref="StatuteKind.OtherStatute"/>. A client sending a number
/// gets a 400, not a statute of the wrong kind.
/// </remarks>
public static class StatuteKinds
{
    public static bool TryParse(string? value, out StatuteKind kind)
    {
        kind = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "labourlaw" => Assigned(StatuteKind.LabourLaw, out kind),
            "otherstatute" => Assigned(StatuteKind.OtherStatute, out kind),
            _ => false,
        };
    }

    private static bool Assigned(StatuteKind value, out StatuteKind kind)
    {
        kind = value;
        return true;
    }
}

public sealed record CreateStatuteRequest(
    string? Kind,
    string? Name,
    DateOnly? PublishedOn,
    string? Source,
    Guid? DocumentId);

/// <summary>
/// A statute's descriptive fields, partially. <c>kind</c> is absent on purpose: it is fixed at
/// creation, because changing it would have to obey the single-labour-law invariant and that is a
/// separate operation.
/// </summary>
/// <remarks>
/// The same rule as the company endpoint: a field that is not sent is left unchanged. The addition is
/// that a field sent as <c>null</c> clears a nullable field (<c>publishedOn</c>, <c>source</c>,
/// <c>documentId</c>), which a plain nullable property could not express. <c>name</c> is required, so
/// sending it as <c>null</c> is a 400 rather than a clear.
/// </remarks>
public sealed record UpdateStatuteRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PatchField<string?> Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PatchField<DateOnly?> PublishedOn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PatchField<string?> Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PatchField<Guid?> DocumentId);

/// <summary>
/// A new statute article: number, its own heading (title) and body are mandatory; the comparison
/// classification is optional under A-12.
/// </summary>
public sealed record StatuteArticleRequest(
    int Number,
    string? Title,
    string? Summary,
    string? Body,
    Guid? HeadingId,
    string? EconomicComparisonNote);

/// <summary>
/// A statute article, partially. A field that is not sent keeps its stored value; a nullable field
/// sent as <c>null</c> is cleared (the classification included, which A-12 allows).
/// </summary>
/// <remarks>
/// <c>number</c>, <c>title</c> and <c>body</c> are required by the aggregate, so sending any of them
/// as <c>null</c> is a 400 rather than a clear. Omitting them is a valid partial update.
/// </remarks>
public sealed record UpdateStatuteArticleRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PatchField<int?> Number,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PatchField<string?> Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PatchField<string?> Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PatchField<string?> Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PatchField<Guid?> HeadingId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    PatchField<string?> EconomicComparisonNote);

public sealed record StatuteSummary(
    Guid Id,
    string Kind,
    string Name,
    DateOnly? PublishedOn,
    string? Source,
    Guid? DocumentId,
    int ArticleCount,
    uint RowVersion);

public sealed record StatuteDetail(
    Guid Id,
    string Kind,
    string Name,
    DateOnly? PublishedOn,
    string? Source,
    Guid? DocumentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint RowVersion,
    IReadOnlyList<StatuteArticleResponse> Articles);

/// <summary>
/// One article. <c>headingName</c> is absent deliberately: resolving it would couple this module to
/// the comparison taxonomy for a display convenience, and the client already has
/// <c>GET /comparison/headings</c>. <c>headingId</c> is the fact; the name is the taxonomy's.
/// </summary>
public sealed record StatuteArticleResponse(
    Guid Id,
    int Number,
    string Title,
    string? Summary,
    string Body,
    Guid? HeadingId,
    string? EconomicComparisonNote,
    uint RowVersion);

public sealed record StatuteListResponse(
    IReadOnlyList<StatuteSummary> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record StatuteArticleListResponse(
    IReadOnlyList<StatuteArticleResponse> Items,
    int Total);

/// <summary>
/// One statute article as the comparison workspace reads it: adjacent to the clauses of an
/// agreement, never paired with them.
/// </summary>
public sealed record StatuteComparisonArticle(
    Guid StatuteId,
    string StatuteName,
    string StatuteKind,
    Guid ArticleId,
    int Number,
    string Title,
    string Body,
    string? EconomicComparisonNote);
