using Convenciones.Agreements;
using Convenciones.Catalog;

namespace Convenciones.Publishing;

/// <summary>
/// A news article for the public portal (BR-25, Q-6).
/// </summary>
/// <remarks>
/// <para>
/// The legacy system stored <c>estatus_noticia</c> as free text holding <c>Pendiente</c> or
/// <c>Publicar</c>, and its create handler wrote <c>Pendiente</c> regardless of the submitted value, so
/// publishing always required a create followed by an edit. The state here is a typed
/// <see cref="PublicationState"/>, and the database's consistency CHECK requires the publication
/// timestamp and actor to exist exactly when the article is published.
/// </para>
/// <para>
/// The body is sanitised on write (<see cref="RichText.Clean"/>, A-33), because a public page echoes it
/// as HTML. The publication actor and timestamp are assigned by the server and re-assigned by a database
/// trigger, so a client cannot supply either.
/// </para>
/// </remarks>
public sealed class NewsArticle : IAuditable
{
    private NewsArticle()
    {
    }

    public Guid Id { get; private set; }

    public Guid CategoryId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Summary { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public PublicationState State { get; private set; } = PublicationState.Draft;

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    public string AuditModule => "publishing";

    public string AuditEntityType => "news_article";

    public string AuditEntityId => Id.ToString();

    public bool IsPublished => State is PublicationState.Published;

    public static NewsArticle Create(
        Guid categoryId,
        string? title,
        string? summary,
        string? body,
        Guid actorId,
        DateTimeOffset now)
    {
        var article = new NewsArticle
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };

        article.Apply(categoryId, title, summary, body, actorId, now);
        return article;
    }

    public void Apply(
        Guid categoryId,
        string? title,
        string? summary,
        string? body,
        Guid actorId,
        DateTimeOffset now)
    {
        if (categoryId == Guid.Empty)
        {
            throw NewsRuleException.Validation("The news category is required.");
        }

        CategoryId = categoryId;
        Title = NewsText.Required(title, "The article title is required.");
        Summary = NewsText.Optional(summary);
        Body = NewsText.RequireBody(body);
        UpdatedBy = actorId;
        UpdatedAt = now;
    }

    /// <summary>
    /// Draft → Published. The reference time and the actor are recorded here and re-assigned by
    /// <c>app.guard_news_publication()</c>, so the value the client sees is the database's.
    /// </summary>
    public void Publish(Guid actorId, DateTimeOffset now)
    {
        if (IsPublished)
        {
            throw NewsRuleException.Conflict(
                "already_published",
                "The article is already published.");
        }

        State = PublicationState.Published;
        PublishedAt = now;
        PublishedBy = actorId;
        UpdatedBy = actorId;
        UpdatedAt = now;
    }

    /// <summary>Published → Draft. Unpublishing removes it from every public read immediately.</summary>
    public void Unpublish(Guid actorId, DateTimeOffset now)
    {
        if (!IsPublished)
        {
            throw NewsRuleException.Conflict(
                "not_published",
                "The article is not published; there is nothing to unpublish.");
        }

        State = PublicationState.Draft;
        PublishedAt = null;
        PublishedBy = null;
        UpdatedBy = actorId;
        UpdatedAt = now;
    }
}

/// <summary>
/// A news category. Reference data: no company, read by the public portal, written by administrators.
/// </summary>
public sealed class NewsCategory
{
    private NewsCategory()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static NewsCategory Create(
        string? name,
        string? description,
        int sortOrder,
        DateTimeOffset now)
    {
        if (sortOrder < 0)
        {
            throw NewsRuleException.Validation("The sort order cannot be negative.");
        }

        return new NewsCategory
        {
            Id = Guid.NewGuid(),
            Name = NewsText.Required(name, "The category name is required."),
            Description = NewsText.Optional(description),
            SortOrder = sortOrder,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
