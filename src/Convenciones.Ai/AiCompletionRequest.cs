namespace Convenciones.Ai;

/// <summary>One prompt sent to the model, with the JSON schema the answer must satisfy.</summary>
/// <remarks>
/// The schema travels with every request so a caller cannot accidentally accept an answer shaped for a
/// different task. <see cref="Deployment"/> is optional: the configured deployment is the default.
/// </remarks>
public sealed record AiCompletionRequest(
    string SystemPrompt,
    string UserPrompt,
    string JsonSchema,
    int? MaxOutputTokens = null,
    string? Deployment = null);
