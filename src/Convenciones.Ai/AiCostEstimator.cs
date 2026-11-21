namespace Convenciones.Ai;

/// <summary>
/// Conservative pre-call estimate used to reserve budget before the provider is contacted.
/// </summary>
/// <remarks>
/// There is no tokenizer in the deployment, so input size is approximated from character count with a
/// deliberately low characters-per-token ratio: over-reserving is safe (the difference is released at
/// settlement), under-reserving is not. The output side uses the request's own <c>MaxOutputTokens</c>,
/// which is the only upper bound the API accepts.
/// </remarks>
public static class AiCostEstimator
{
    public const int CharactersPerToken = 3;

    public static long EstimateInputTokens(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return (prompt.Length + CharactersPerToken - 1) / CharactersPerToken;
    }

    /// <summary>Maximum expected cost of one invocation under <paramref name="pricing"/>.</summary>
    public static decimal ReserveEstimate(
        AiPricing pricing,
        string systemPrompt,
        string userPrompt,
        int maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputTokens);

        var inputTokens = EstimateInputTokens(systemPrompt) + EstimateInputTokens(userPrompt);
        return pricing.EstimateCost((int)inputTokens, maxOutputTokens) ?? 0m;
    }
}
