namespace Convenciones.Publishing;

public enum NewsCommandStatus
{
    Ok,
    Created,
    NotFound,
    ValidationFailed,
    Conflict,
}

/// <summary>The outcome of a publishing command, in domain terms rather than HTTP terms.</summary>
public sealed record NewsCommandResult(
    NewsCommandStatus Status,
    string? Detail = null,
    NewsDetail? Article = null,
    NewsCategoryResponse? Category = null)
{
    public static NewsCommandResult Ok(NewsDetail article) =>
        new(NewsCommandStatus.Ok, Article: article);

    public static NewsCommandResult Created(NewsDetail article) =>
        new(NewsCommandStatus.Created, Article: article);

    public static NewsCommandResult CreatedCategory(NewsCategoryResponse category) =>
        new(NewsCommandStatus.Created, Category: category);

    public static NewsCommandResult NotFound(string detail) =>
        new(NewsCommandStatus.NotFound, detail);

    public static NewsCommandResult Validation(string detail) =>
        new(NewsCommandStatus.ValidationFailed, detail);

    public static NewsCommandResult Conflict(string detail) =>
        new(NewsCommandStatus.Conflict, detail);

    internal static NewsCommandResult FromRule(NewsRuleException rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Kind is NewsRuleKind.Validation
            ? Validation(rule.Message)
            : Conflict(rule.Message);
    }
}

internal static class PublishingCommandHelpers
{
    /// <summary>Loads the category name once, because the response carries it and the aggregate does not.</summary>
    public static async Task<NewsDetail> DetailAsync(
        INewsStore store,
        NewsArticle article,
        CancellationToken cancellationToken) =>
        NewsResponses.Detail(
            article,
            await store.CategoryNameAsync(article.CategoryId, cancellationToken).ConfigureAwait(false)
            ?? "Sin categoría");
}
