namespace Convenciones.Comparison;

/// <summary>One agreement that has at least one clause under the chosen heading.</summary>
public sealed record ComparisonCandidateRow(
    Guid AgreementId,
    Guid CompanyId,
    string CompanyName,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    int ClauseCount);

/// <summary>The response row: the stored fields plus the Q-1 title composed on read.</summary>
public sealed record ComparisonCandidate(
    Guid AgreementId,
    Guid CompanyId,
    string CompanyName,
    string Title,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    int ClauseCount);

public sealed record ComparisonCandidatePage(
    IReadOnlyList<ComparisonCandidate> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record ComparisonHeadingView(
    Guid Id,
    Guid GroupId,
    string GroupName,
    string Name,
    bool IsExcluded,
    bool RequiresEconomicComparison,
    int SortOrder);

public sealed record ComparisonClauseView(
    Guid ClauseId,
    int Number,
    string Title,
    string? Summary,
    string Body,
    string? EconomicComparisonNote,
    bool IsExcludedFromCompilation);

public sealed record ComparisonAgreementView(
    Guid AgreementId,
    Guid CompanyId,
    string CompanyName,
    string Title,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    IReadOnlyList<ComparisonClauseView> Clauses);

/// <summary>
/// A statute article classified under the compared heading — the legal side of the workspace.
/// </summary>
/// <remarks>
/// The field existed in T9, empty, so that filling it was not a contract change; T10 fills it. The
/// number is an integer because clause and article numbering is integer-only (Q-2). Articles are
/// <b>adjacent</b> to the agreements, never paired with a clause: no clause-to-article alignment
/// exists (Q-6).
/// </remarks>
public sealed record ComparisonStatuteArticleView(
    Guid StatuteId,
    string StatuteName,
    string StatuteKind,
    Guid ArticleId,
    int Number,
    string Title,
    string Body,
    string? EconomicComparisonNote);

public sealed record ComparisonView(
    ComparisonHeadingView Heading,
    IReadOnlyList<ComparisonStatuteArticleView> StatuteArticles,
    int StatuteArticleTotal,
    IReadOnlyList<ComparisonAgreementView> Agreements,
    IReadOnlyList<Guid> UnpublishedRequested,
    int Page,
    int PageSize,
    int Total);

public sealed record HeadingGroupView(
    Guid Id,
    string Name,
    string? Description,
    bool RequiresEconomicComparison,
    int SortOrder);
