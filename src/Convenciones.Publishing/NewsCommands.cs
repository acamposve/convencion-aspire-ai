using Convenciones.Catalog;

namespace Convenciones.Publishing;

/// <summary>Creates a news article. It always starts in <c>Draft</c> (the legacy create ignored the state).</summary>
public sealed class CreateNewsHandler(INewsStore store)
{
    public async Task<NewsCommandResult> HandleAsync(
        CreateNewsRequest request,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await store.CategoryExistsAsync(request.CategoryId, cancellationToken).ConfigureAwait(false))
        {
            return NewsCommandResult.Validation("The news category does not exist.");
        }

        NewsArticle article;

        try
        {
            article = NewsArticle.Create(
                request.CategoryId,
                request.Title,
                request.Summary,
                request.Body,
                actorId,
                DateTimeOffset.UtcNow);
        }
        catch (NewsRuleException rule)
        {
            return NewsCommandResult.FromRule(rule);
        }

        store.Add(article);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NewsCommandResult.Created(
            await PublishingCommandHelpers.DetailAsync(store, article, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Applies a partial update. <c>PATCH</c> semantics come from <see cref="PatchField{T}"/>: an absent field
/// keeps its value, an explicit <c>null</c> clears a nullable one, and <c>null</c> for a required field is
/// a 400 — so a rename cannot erase the body and a summary can still be cleared deliberately.
/// </summary>
public sealed class UpdateNewsHandler(INewsStore store)
{
    public async Task<NewsCommandResult> HandleAsync(
        Guid articleId,
        UpdateNewsRequest request,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var article = await store.FindAsync(articleId, cancellationToken).ConfigureAwait(false);

        if (article is null)
        {
            return NewsCommandResult.NotFound($"No news article with id '{articleId}' exists.");
        }

        var categoryId = article.CategoryId;

        if (request.CategoryId.IsSet)
        {
            if (request.CategoryId.Value is not { } requestedCategory)
            {
                return NewsCommandResult.Validation("The news category cannot be cleared.");
            }

            if (!await store.CategoryExistsAsync(requestedCategory, cancellationToken).ConfigureAwait(false))
            {
                return NewsCommandResult.Validation("The news category does not exist.");
            }

            categoryId = requestedCategory;
        }

        if (request.Title.IsSet && request.Title.Value is null)
        {
            return NewsCommandResult.Validation("The article title is required.");
        }

        if (request.Body.IsSet && request.Body.Value is null)
        {
            return NewsCommandResult.Validation("The article body is required.");
        }

        try
        {
            article.Apply(
                categoryId,
                request.Title.IsSet ? request.Title.Value : article.Title,
                request.Summary.IsSet ? request.Summary.Value : article.Summary,
                request.Body.IsSet ? request.Body.Value : article.Body,
                actorId,
                DateTimeOffset.UtcNow);
        }
        catch (NewsRuleException rule)
        {
            return NewsCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NewsCommandResult.Ok(
            await PublishingCommandHelpers.DetailAsync(store, article, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Draft → Published, behind <c>news.publish</c>. The actor and the time are assigned here and
/// re-assigned by the database trigger, so the client cannot supply either.
/// </summary>
public sealed class PublishNewsHandler(INewsStore store)
{
    public async Task<NewsCommandResult> HandleAsync(
        Guid articleId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var article = await store.FindAsync(articleId, cancellationToken).ConfigureAwait(false);

        if (article is null)
        {
            return NewsCommandResult.NotFound($"No news article with id '{articleId}' exists.");
        }

        try
        {
            article.Publish(actorId, DateTimeOffset.UtcNow);
        }
        catch (NewsRuleException rule)
        {
            return NewsCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NewsCommandResult.Ok(
            await PublishingCommandHelpers.DetailAsync(store, article, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>Published → Draft. The public reads stop returning the article immediately.</summary>
public sealed class UnpublishNewsHandler(INewsStore store)
{
    public async Task<NewsCommandResult> HandleAsync(
        Guid articleId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var article = await store.FindAsync(articleId, cancellationToken).ConfigureAwait(false);

        if (article is null)
        {
            return NewsCommandResult.NotFound($"No news article with id '{articleId}' exists.");
        }

        try
        {
            article.Unpublish(actorId, DateTimeOffset.UtcNow);
        }
        catch (NewsRuleException rule)
        {
            return NewsCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NewsCommandResult.Ok(
            await PublishingCommandHelpers.DetailAsync(store, article, cancellationToken).ConfigureAwait(false));
    }
}

public sealed class CreateNewsCategoryHandler(INewsStore store)
{
    public async Task<NewsCommandResult> HandleAsync(
        CreateNewsCategoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        NewsCategory category;

        try
        {
            category = NewsCategory.Create(
                request.Name, request.Description, request.SortOrder, DateTimeOffset.UtcNow);
        }
        catch (NewsRuleException rule)
        {
            return NewsCommandResult.FromRule(rule);
        }

        store.Add(category);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return NewsCommandResult.CreatedCategory(NewsResponses.Category(category));
    }
}
