namespace Convenciones.Ai;

/// <summary>
/// The single boundary between the application and a language model.
/// </summary>
/// <remarks>
/// Business code never sees a provider type: the deployment and the endpoint are configuration, and
/// every answer is validated against the schema before it is returned. Frozen by the architecture
/// decision in <c>docs/architecture/AZURE-AI-CLAUSE-EXTRACTION-AND-CLASSIFICATION.md</c>.
/// </remarks>
public interface IAiModelClient
{
    Task<AiCompletionResult> CompleteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default);
}
