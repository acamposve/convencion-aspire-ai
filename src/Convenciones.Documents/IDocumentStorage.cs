namespace Convenciones.Documents;

/// <summary>
/// Where document bytes live. The port exists so the working adapter is replaceable without the API
/// knowing: a private directory today, Supabase Storage once a project is approved (T18).
/// </summary>
/// <remarks>
/// Deliberately has no method that returns a public URL. Downloads are served by the API after a
/// capability check (T6), so that no object is reachable by knowing its key.
/// </remarks>
public interface IDocumentStorage
{
    /// <summary>Writes the object. Returns the key that was used.</summary>
    Task SaveAsync(string key, Stream content, CancellationToken cancellationToken);

    /// <summary>Opens the object, or <see langword="null"/> when it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes the object. Used to compensate for a failed database write.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
