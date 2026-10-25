namespace Convenciones.Legal;

/// <summary>The values a partial statute update resolves to: sent fields replaced, omitted ones kept.</summary>
public sealed record StatuteHeaderEdit(
    string? Name,
    DateOnly? PublishedOn,
    string? Source,
    Guid? DocumentId);

/// <summary>The values a partial article update resolves to. A null number or title is invalid.</summary>
public sealed record StatuteArticleEdit(
    int? Number,
    string? Title,
    string? Summary,
    string? Body,
    Guid? HeadingId,
    string? EconomicComparisonNote);

/// <summary>
/// Resolves a partial update against the current state, in one place so the rule can be tested
/// without a server or a database.
/// </summary>
/// <remarks>
/// The merge is what makes <c>PATCH</c> partial: an <see cref="PatchField{T}"/> that was not sent
/// keeps the stored value, a field sent as <c>null</c> becomes <c>null</c>, and a field sent with a
/// value replaces it. Validation still happens in the aggregate, on the merged values, so a request
/// that sets a required field to null is refused exactly as a create would be.
/// </remarks>
public static class StatutePatch
{
    public static StatuteHeaderEdit Merge(Statute statute, UpdateStatuteRequest request)
    {
        ArgumentNullException.ThrowIfNull(statute);
        ArgumentNullException.ThrowIfNull(request);

        return new StatuteHeaderEdit(
            request.Name.IsSet ? request.Name.Value : statute.Name,
            request.PublishedOn.IsSet ? request.PublishedOn.Value : statute.PublishedOn,
            request.Source.IsSet ? request.Source.Value : statute.Source,
            request.DocumentId.IsSet ? request.DocumentId.Value : statute.DocumentId);
    }

    public static StatuteArticleEdit Merge(StatuteArticle article, UpdateStatuteArticleRequest request)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(request);

        return new StatuteArticleEdit(
            request.Number.IsSet ? request.Number.Value : article.Number,
            request.Title.IsSet ? request.Title.Value : article.Title,
            request.Summary.IsSet ? request.Summary.Value : article.Summary,
            request.Body.IsSet ? request.Body.Value : article.Body,
            request.HeadingId.IsSet ? request.HeadingId.Value : article.HeadingId,
            request.EconomicComparisonNote.IsSet
                ? request.EconomicComparisonNote.Value
                : article.EconomicComparisonNote);
    }
}
