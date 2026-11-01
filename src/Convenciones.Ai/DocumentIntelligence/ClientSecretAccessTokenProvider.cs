using Azure.Core;
using Azure.Identity;

namespace Convenciones.Ai.DocumentIntelligence;

/// <summary>
/// Entra client-credentials token provider: the recommended way to call the AI resources from the
/// Droplet. The secret is configuration, never code; tokens are cached and renewed by Azure.Identity.
/// </summary>
public sealed class ClientSecretAccessTokenProvider : IAccessTokenProvider
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly ClientSecretCredential _credential;

    public ClientSecretAccessTokenProvider(string tenantId, string clientId, string clientSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        _credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = await _credential
            .GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken)
            .ConfigureAwait(false);

        return token.Token;
    }
}
