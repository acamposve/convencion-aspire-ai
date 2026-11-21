using System.Text.Json;

namespace Convenciones.Ai;

/// <summary>A validated model answer and the accounting the caller must persist.</summary>
/// <remarks>
/// <see cref="Content"/> is cloned out of the response document, so the caller owns it and does not
/// depend on the lifetime of an internal <see cref="JsonDocument"/>.
/// </remarks>
public sealed record AiCompletionResult(
    JsonElement Content,
    string Model,
    string Deployment,
    int? InputTokens,
    int? OutputTokens,
    TimeSpan Latency,
    string Provider = "azure-foundry");
