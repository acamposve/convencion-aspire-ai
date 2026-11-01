namespace Convenciones.Ai.DocumentIntelligence;

/// <summary>Reads the layout (pages, paragraphs, spans, boxes) of a PDF.</summary>
public interface IDocumentLayoutClient
{
    Task<DocumentLayout> AnalyzeAsync(Stream pdf, CancellationToken cancellationToken = default);
}

/// <summary>Access token for a cognitive-services scope; implemented by Entra or by a test double.</summary>
public interface IAccessTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Configuration for Azure AI Document Intelligence.</summary>
public sealed class AzureDocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";

    public Uri? Endpoint { get; set; }

    /// <summary>API key fallback. Prefer Entra (<see cref="TenantId"/>/<see cref="ClientId"/>/<see cref="ClientSecret"/>).</summary>
    public string? ApiKey { get; set; }

    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string ApiVersion { get; set; } = "2024-11-30";

    public string ModelId { get; set; } = "prebuilt-layout";

    /// <summary>Overall LRO deadline, including polling.</summary>
    public int TimeoutSeconds { get; set; } = 90;

    public int PollIntervalMilliseconds { get; set; } = 1000;

    /// <summary>Average non-whitespace characters per page below which OCR is required.</summary>
    public int MinCharactersPerPage { get; set; } = 50;

    public void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri)
        {
            throw new AiConfigurationException("DocumentIntelligence:Endpoint must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(ModelId))
        {
            throw new AiConfigurationException("DocumentIntelligence:ModelId is required.");
        }

        if (TimeoutSeconds <= 0 || PollIntervalMilliseconds <= 0 || MinCharactersPerPage <= 0)
        {
            throw new AiConfigurationException("DocumentIntelligence timeouts and thresholds must be positive.");
        }

        var hasApiKey = !string.IsNullOrWhiteSpace(ApiKey);
        var hasEntra = !string.IsNullOrWhiteSpace(TenantId)
            && !string.IsNullOrWhiteSpace(ClientId)
            && !string.IsNullOrWhiteSpace(ClientSecret);

        if (!hasApiKey && !hasEntra)
        {
            throw new AiConfigurationException(
                "DocumentIntelligence requires ApiKey or TenantId/ClientId/ClientSecret.");
        }
    }
}
