namespace Convenciones.Identity;

/// <summary>
/// Token validation settings. There are two supported modes and no default: a symmetric key for
/// local development and tests, or a metadata endpoint (Supabase JWKS) for a real deployment.
/// </summary>
/// <remarks>
/// Startup fails when neither is configured. An API that accepts any token because nobody configured
/// validation is worse than an API that refuses to start: it looks like it is working.
/// </remarks>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Expected <c>iss</c>. Required.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Expected <c>aud</c>. Required.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// HS256 key for local development and tests. Never used in a deployment: a shared secret in
    /// configuration can be read by anyone who can read the configuration.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>Issuer metadata address (Supabase: <c>/auth/v1/.well-known/jwks.json</c>).</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>Authority, when the metadata address is discoverable from it.</summary>
    public string? Authority { get; set; }

    public bool HasSigningKey => !string.IsNullOrWhiteSpace(SigningKey);

    public bool HasMetadataEndpoint =>
        !string.IsNullOrWhiteSpace(MetadataAddress) || !string.IsNullOrWhiteSpace(Authority);
}
