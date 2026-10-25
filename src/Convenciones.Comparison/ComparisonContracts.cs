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
/// Statute articles are T10. The field is in the shape now so adding them is not a contract change;
/// it is empty until the legal library exists.
/// </summary>
public sealed record ComparisonStatuteArticleView(
    Guid StatuteId,
    string StatuteName,
    string ArticleNumber,
    string Title,
    string Body);

public sealed record ComparisonView(
    ComparisonHeadingView Heading,
    IReadOnlyList<ComparisonStatuteArticleView> StatuteArticles,
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
