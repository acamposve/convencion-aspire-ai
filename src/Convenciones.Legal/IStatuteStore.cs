namespace Convenciones.Legal;

/// <summary>
/// What the legal library needs from persistence — and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// The port exists because the module owns its commands (constitution Art. IV.4) while the
/// single <c>DbContext</c> belongs to the API host (Art. IV.6: a module never reaches into another
/// module's persistence). The API supplies the only implementation, over that context.
/// </para>
/// <para>
/// Unique violations are <b>not</b> interpreted here: the implementation lets the provider's exception
/// surface, and the endpoint translates it into a 409 naming the constraint that refused the write.
/// Swallowing it here would hide which index is the guard, and for the single-labour-law rule the
/// index is the guard.
/// </para>
/// </remarks>
public interface IStatuteStore
{
    /// <summary>
    /// Loads the aggregate with its articles, tracked, so a command can change it.
    /// </summary>
    /// <returns><see langword="null"/> when no statute has that id.</returns>
    Task<Statute?> FindAsync(Guid statuteId, CancellationToken cancellationToken);

    /// <summary>Whether the heading exists and is active — the classification an article may carry.</summary>
    Task<bool> HeadingIsActiveAsync(Guid headingId, CancellationToken cancellationToken);

    /// <summary>Whether the document exists and has not been deleted.</summary>
    Task<bool> DocumentIsAvailableAsync(Guid documentId, CancellationToken cancellationToken);

    void Add(Statute statute);

    /// <summary>
    /// Registers a new article explicitly. It already has its key, so adding it through the parent's
    /// navigation alone would make the provider treat it as an existing row and issue an UPDATE.
    /// </summary>
    void Add(StatuteArticle article);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
