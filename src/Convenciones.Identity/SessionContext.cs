namespace Convenciones.Identity;

/// <summary>
/// What the caller is allowed to do, resolved from the database — never from the token.
/// </summary>
/// <remarks>
/// Capabilities are read from <c>platform.role_capability</c> on every request rather than embedded
/// in the JWT. A revoked capability therefore takes effect on the next request instead of when the
/// token expires, which is the difference between a permission system and a suggestion.
/// </remarks>
public sealed record SessionContext(
    Guid UserId,
    string Email,
    string? DisplayName,
    Guid? CompanyId,
    bool IsActive,
    bool IsBlocked,
    DateTimeOffset? ExpiresOn,
    IReadOnlySet<string> Capabilities)
{
    public bool HasCapability(string capability) =>
        !string.IsNullOrWhiteSpace(capability) && Capabilities.Contains(capability);

    /// <summary>Why this session is not usable, for a log — never returned to the caller.</summary>
    public string? InactiveReason
    {
        get
        {
            if (IsActive)
            {
                return null;
            }

            if (IsBlocked)
            {
                return "account is blocked";
            }

            return ExpiresOn is { } expires && expires <= DateTimeOffset.UtcNow
                ? "account has expired"
                : "account is not active";
        }
    }
}
