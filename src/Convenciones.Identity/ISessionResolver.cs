namespace Convenciones.Identity;

/// <summary>The database role an anonymous request must run as.</summary>
public enum RequestDatabaseRole
{
    /// <summary>No session and no declaration: reset to the login's role.</summary>
    Default = 0,

    /// <summary>Run as <c>app_api</c> with no claims, so the RLS window policies apply (bootstrap).</summary>
    Api = 1,

    /// <summary>Run as <c>anon</c>, so only the public policies apply.</summary>
    Anonymous = 2,
}

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
    /// The database role an anonymous request must run as: <c>Api</c> for bootstrap (RLS without
    /// claims), <c>Anonymous</c> for the public portal, <c>Default</c> otherwise.
    /// </summary>
    RequestDatabaseRole DatabaseRole { get; set; }
}

internal sealed class SessionAccessor : ISessionAccessor
{
    public SessionContext? Session { get; set; }

    public RequestDatabaseRole DatabaseRole { get; set; }
}
