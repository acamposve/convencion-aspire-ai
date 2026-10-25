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
}

internal sealed class SessionAccessor : ISessionAccessor
{
    public SessionContext? Session { get; set; }
}
