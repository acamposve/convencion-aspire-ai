namespace Convenciones.Identity;

/// <summary>
/// Resolves the caller's session from the database.
/// </summary>
public interface ISessionResolver
{
    /// <summary>
    /// Returns the session for <paramref name="userId"/>, or <see langword="null"/> when no account
    /// exists — which is not the same as an inactive account, and is reported differently.
    /// </summary>
    Task<SessionContext?> ResolveAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Carries the session resolved for the current request.
/// </summary>
public interface ISessionAccessor
{
    SessionContext? Session { get; set; }

    /// <summary>
    /// True when the endpoint declared that it serves public data. The database connection then
    /// runs as <c>anon</c> instead of the privileged login, so the public RLS policies filter.
    /// </summary>
    bool UseAnonymousDatabaseRole { get; set; }
}

internal sealed class SessionAccessor : ISessionAccessor
{
    public SessionContext? Session { get; set; }

    public bool UseAnonymousDatabaseRole { get; set; }
}
