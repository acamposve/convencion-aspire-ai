namespace Convenciones.Ai;

/// <summary>Configuration for the Azure AI Foundry model client.</summary>
/// <remarks>
/// The endpoint, deployment and credential are configuration, never code. Authentication is either an
/// API key (<c>api-key</c> header) or a bearer token obtained elsewhere; the client does not acquire
/// Entra tokens itself, so no identity library is pulled into this module.
/// </remarks>
public sealed class AzureAiOptions
{
    public const string SectionName = "AzureAi";

    /// <summary>Base endpoint, for example <c>https://ai-example.cognitiveservices.azure.com/</c>.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Deployment name created in Azure AI Foundry.</summary>
    public string Deployment { get; set; } = string.Empty;

    /// <summary>API key. Never log it, never commit it.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Bearer token, when the caller already acquired one through Entra.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Azure AI Foundry model-inference API version.</summary>
    public string ApiVersion { get; set; } = "2024-05-01-preview";

    /// <summary>Chat-completions path. The default matches the model-inference endpoint.</summary>
    public string ChatCompletionsPath { get; set; } = "/models/chat/completions";

    /// <summary>Per-attempt timeout. The worker owns the overall run timeout.</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>Transient attempts (429/5xx/timeouts handled by the caller) before giving up.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Request <c>json_schema</c> when the model supports it; otherwise JSON mode plus application-side
    /// schema validation. Mistral on Foundry currently fits the latter.
    /// </summary>
    public bool UseJsonSchema { get; set; }

    public double Temperature { get; set; }

    /// <summary>Fails fast with a message that never contains a secret.</summary>
    public void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri)
        {
            throw new AiConfigurationException("AzureAi:Endpoint must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(Deployment))
        {
            throw new AiConfigurationException("AzureAi:Deployment is required.");
        }

        if (string.IsNullOrWhiteSpace(ApiKey) && string.IsNullOrWhiteSpace(AccessToken))
        {
            throw new AiConfigurationException("AzureAi requires ApiKey or AccessToken.");
        }

        if (string.IsNullOrWhiteSpace(ApiVersion))
        {
            throw new AiConfigurationException("AzureAi:ApiVersion is required.");
        }

        if (TimeoutSeconds <= 0)
        {
            throw new AiConfigurationException("AzureAi:TimeoutSeconds must be positive.");
        }

        if (MaxAttempts <= 0)
        {
            throw new AiConfigurationException("AzureAi:MaxAttempts must be positive.");
        }
    }
}
