namespace Convenciones.Legal;

/// <summary>The outcome of a legal-library command, in domain terms rather than HTTP terms.</summary>
/// <remarks>
/// Keeping HTTP out of the module means the same handlers are exercised by unit tests without a
/// server, and the endpoint is the only place that decides a status code. Mirrors the way the
/// agreement endpoints translate <c>AgreementRuleException</c>.
/// </remarks>
public enum StatuteCommandStatus
{
    Created,
    Ok,
    NoContent,
    NotFound,
    ValidationFailed,
    Conflict,
    InvalidState,
    ConcurrentModification,
}

public sealed record StatuteCommandResult(
    StatuteCommandStatus Status,
    string? Detail = null,
    StatuteDetail? Statute = null,
    StatuteArticleResponse? Article = null)
{
    public static StatuteCommandResult StatuteCreated(StatuteDetail statute) =>
        new(StatuteCommandStatus.Created, Statute: statute);

    public static StatuteCommandResult StatuteUpdated(StatuteDetail statute) =>
        new(StatuteCommandStatus.Ok, Statute: statute);

    public static StatuteCommandResult ArticleCreated(StatuteArticleResponse article) =>
        new(StatuteCommandStatus.Created, Article: article);

    public static StatuteCommandResult ArticleUpdated(StatuteArticleResponse article) =>
        new(StatuteCommandStatus.Ok, Article: article);

    public static StatuteCommandResult Deleted() => new(StatuteCommandStatus.NoContent);

    public static StatuteCommandResult NotFound(string detail) =>
        new(StatuteCommandStatus.NotFound, detail);

    public static StatuteCommandResult Validation(string detail) =>
        new(StatuteCommandStatus.ValidationFailed, detail);

    public static StatuteCommandResult Conflict(string detail) =>
        new(StatuteCommandStatus.Conflict, detail);

    public static StatuteCommandResult ConcurrentModification() =>
        new(
            StatuteCommandStatus.ConcurrentModification,
            "The resource was modified by someone else. Re-read it and retry with the current "
            + "rowVersion.");

    internal static StatuteCommandResult FromRule(StatuteRuleException rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Kind switch
        {
            StatuteRuleKind.Validation => Validation(rule.Message),
            StatuteRuleKind.Conflict => Conflict(rule.Message),
            _ => new StatuteCommandResult(StatuteCommandStatus.InvalidState, rule.Message),
        };
    }
}

/// <summary>Creates a statute.</summary>
/// <remarks>
/// A second <see cref="StatuteKind.LabourLaw"/> is not refused here: the partial unique index
/// <c>statute_single_labour_law_key</c> refuses it, and the endpoint reports the 409 that names that
/// index. A pre-check in this handler would race with a concurrent create and would make the test
/// unable to prove the index is the guard (T10 acceptance criterion 1).
/// </remarks>
public sealed class CreateStatuteHandler(IStatuteStore store)
{
    public async Task<StatuteCommandResult> HandleAsync(
        CreateStatuteRequest request,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!StatuteKinds.TryParse(request.Kind, out var kind))
        {
            return StatuteCommandResult.Validation(
                "The statute kind must be 'LabourLaw' or 'OtherStatute'.");
        }

        if (request.DocumentId is { } documentId
            && !await store.DocumentIsAvailableAsync(documentId, cancellationToken).ConfigureAwait(false))
        {
            return StatuteCommandResult.Validation(
                "The referenced document does not exist or was deleted.");
        }

        Statute statute;

        try
        {
            statute = Statute.Create(
                kind,
                request.Name,
                request.PublishedOn,
                request.Source,
                request.DocumentId,
                actorId,
                DateTimeOffset.UtcNow);
        }
        catch (StatuteRuleException rule)
        {
            return StatuteCommandResult.FromRule(rule);
        }

        store.Add(statute);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return StatuteCommandResult.StatuteCreated(StatuteResponses.Detail(statute));
    }
}

/// <summary>Changes a statute's descriptive fields. The kind is fixed at creation.</summary>
public sealed class UpdateStatuteHandler(IStatuteStore store)
{
    public async Task<StatuteCommandResult> HandleAsync(
        Guid statuteId,
        UpdateStatuteRequest request,
        uint expectedVersion,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var statute = await store.FindAsync(statuteId, cancellationToken).ConfigureAwait(false);

        if (statute is null)
        {
            return StatuteCommandResult.NotFound(DescribeMissingStatute(statuteId));
        }

        if (statute.RowVersion != expectedVersion)
        {
            return StatuteCommandResult.ConcurrentModification();
        }

        // A partial update: omitted fields keep their stored value, explicit nulls clear nullable ones.
        var edit = StatutePatch.Merge(statute, request);

        if (edit.DocumentId != statute.DocumentId
            && edit.DocumentId is { } documentId
            && !await store.DocumentIsAvailableAsync(documentId, cancellationToken).ConfigureAwait(false))
        {
            return StatuteCommandResult.Validation(
                "The referenced document does not exist or was deleted.");
        }

        try
        {
            statute.UpdateHeader(
                edit.Name,
                edit.PublishedOn,
                edit.Source,
                edit.DocumentId,
                actorId,
                DateTimeOffset.UtcNow);
        }
        catch (StatuteRuleException rule)
        {
            return StatuteCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return StatuteCommandResult.StatuteUpdated(StatuteResponses.Detail(statute));
    }

    internal static string DescribeMissingStatute(Guid statuteId) =>
        $"No statute with id '{statuteId}' exists.";
}

/// <summary>Adds an article to a statute.</summary>
public sealed class AddStatuteArticleHandler(IStatuteStore store)
{
    public async Task<StatuteCommandResult> HandleAsync(
        Guid statuteId,
        StatuteArticleRequest request,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var statute = await store.FindAsync(statuteId, cancellationToken).ConfigureAwait(false);

        if (statute is null)
        {
            return StatuteCommandResult.NotFound(UpdateStatuteHandler.DescribeMissingStatute(statuteId));
        }

        if (!await HeadingIsAvailableAsync(request.HeadingId, cancellationToken).ConfigureAwait(false))
        {
            return StatuteCommandResult.Validation(
                "The comparison heading does not exist or is inactive.");
        }

        StatuteArticle article;

        try
        {
            article = statute.AddArticle(
                request.Number,
                request.Title,
                request.Summary,
                request.Body,
                request.HeadingId,
                request.EconomicComparisonNote,
                actorId,
                DateTimeOffset.UtcNow);
        }
        catch (StatuteRuleException rule)
        {
            return StatuteCommandResult.FromRule(rule);
        }

        store.Add(article);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return StatuteCommandResult.ArticleCreated(StatuteResponses.Article(article));
    }

    private async Task<bool> HeadingIsAvailableAsync(Guid? headingId, CancellationToken cancellationToken) =>
        headingId is not { } id
        || await store.HeadingIsActiveAsync(id, cancellationToken).ConfigureAwait(false);
}

/// <summary>Replaces an article's content.</summary>
public sealed class UpdateStatuteArticleHandler(IStatuteStore store)
{
    public async Task<StatuteCommandResult> HandleAsync(
        Guid statuteId,
        Guid articleId,
        UpdateStatuteArticleRequest request,
        uint expectedVersion,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var statute = await store.FindAsync(statuteId, cancellationToken).ConfigureAwait(false);

        if (statute is null)
        {
            return StatuteCommandResult.NotFound(UpdateStatuteHandler.DescribeMissingStatute(statuteId));
        }

        var article = statute.FindArticle(articleId);
        if (article is null)
        {
            return StatuteCommandResult.NotFound(DescribeMissingArticle(articleId));
        }

        if (article.RowVersion != expectedVersion)
        {
            return StatuteCommandResult.ConcurrentModification();
        }

        // A partial update: omitted fields keep their stored value, explicit nulls clear nullable ones.
        var edit = StatutePatch.Merge(article, request);

        // Sending a required field as null is a rejected request, not a clear.
        if (edit.Number is not { } number)
        {
            return StatuteCommandResult.Validation(
                "The article number is required and cannot be cleared.");
        }

        if (edit.HeadingId != article.HeadingId
            && edit.HeadingId is { } headingId
            && !await store.HeadingIsActiveAsync(headingId, cancellationToken).ConfigureAwait(false))
        {
            return StatuteCommandResult.Validation(
                "The comparison heading does not exist or is inactive.");
        }

        try
        {
            statute.EnsureNumberAvailable(number, article.Id);
            article.Update(
                number,
                edit.Title,
                edit.Summary,
                edit.Body,
                edit.HeadingId,
                edit.EconomicComparisonNote,
                actorId,
                DateTimeOffset.UtcNow);
        }
        catch (StatuteRuleException rule)
        {
            return StatuteCommandResult.FromRule(rule);
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return StatuteCommandResult.ArticleUpdated(StatuteResponses.Article(article));
    }

    internal static string DescribeMissingArticle(Guid articleId) =>
        $"No statute article with id '{articleId}' exists in this statute.";
}

/// <summary>Removes an article from a statute.</summary>
public sealed class DeleteStatuteArticleHandler(IStatuteStore store)
{
    public async Task<StatuteCommandResult> HandleAsync(
        Guid statuteId,
        Guid articleId,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        var statute = await store.FindAsync(statuteId, cancellationToken).ConfigureAwait(false);

        if (statute is null)
        {
            return StatuteCommandResult.NotFound(UpdateStatuteHandler.DescribeMissingStatute(statuteId));
        }

        var article = statute.FindArticle(articleId);
        if (article is null)
        {
            return StatuteCommandResult.NotFound(
                UpdateStatuteArticleHandler.DescribeMissingArticle(articleId));
        }

        if (article.RowVersion != expectedVersion)
        {
            return StatuteCommandResult.ConcurrentModification();
        }

        // Removing it from the aggregate is the whole operation: the relationship is required and
        // cascade-deletes, so the provider removes the row and the audit interceptor records it.
        statute.RemoveArticle(article);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return StatuteCommandResult.Deleted();
    }
}
