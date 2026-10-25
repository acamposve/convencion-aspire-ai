namespace Convenciones.Publishing;

// ---- Admin requests ------------------------------------------------------------------------------

public sealed record CreateNewsRequest(Guid CategoryId, string? Title, string? Summary, string? Body);

/// <summary>
/// A partial update: an absent field keeps its value; an explicit <c>null</c> clears a nullable field.
/// <c>title</c> and <c>body</c> are required, so sending <c>null</c> for either is a 400.
/// </summary>
public sealed record UpdateNewsRequest(
    Catalog.PatchField<Guid?> CategoryId,
    Catalog.PatchField<string?> Title,
    Catalog.PatchField<string?> Summary,
    Catalog.PatchField<string?> Body);

public sealed record CreateNewsCategoryRequest(string? Name, string? Description, int SortOrder);

// ---- Admin responses -----------------------------------------------------------------------------

public sealed record NewsSummary(
    Guid Id,
    string Title,
    string? Summary,
    Guid CategoryId,
    string CategoryName,
    string State,
    DateTimeOffset? PublishedAt,
    DateTimeOffset UpdatedAt);

public sealed record NewsDetail(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    string Title,
    string? Summary,
    string Body,
    string State,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record NewsListResponse(
    IReadOnlyList<NewsSummary> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record NewsCategoryResponse(
    Guid Id,
    string Name,
    string? Description,
    int SortOrder,
    bool IsActive);

// ---- Public projections --------------------------------------------------------------------------
//
// Explicit allow-lists, never a serialised entity: the public surface is the only unauthenticated one,
// so a field added here is a disclosure decision. No company ids, no internal identifiers beyond the
// resource id, no publisher identity, no document ids, no draft content.

public sealed record PublicClause(int Number, string Title, string? Summary, string Body);

public sealed record PublicAnnex(string Name, string? Description, string? Body);

public sealed record PublicAgreementSummary(
    Guid Id,
    string Title,
    string CompanyName,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string Scope,
    DateTimeOffset PublishedAt);

public sealed record PublicAgreementDetail(
    Guid Id,
    string Title,
    string CompanyName,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string Scope,
    DateTimeOffset PublishedAt,
    IReadOnlyList<PublicClause> Clauses,
    IReadOnlyList<PublicAnnex> Annexes);

public sealed record PublicAgreementListResponse(
    IReadOnlyList<PublicAgreementSummary> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record PublicNewsSummary(
    Guid Id,
    string Title,
    string? Summary,
    string CategoryName,
    DateTimeOffset PublishedAt);

public sealed record PublicNewsDetail(
    Guid Id,
    string Title,
    string? Summary,
    string Body,
    string CategoryName,
    DateTimeOffset PublishedAt);

public sealed record PublicNewsListResponse(
    IReadOnlyList<PublicNewsSummary> Items,
    int Page,
    int PageSize,
    int Total);
