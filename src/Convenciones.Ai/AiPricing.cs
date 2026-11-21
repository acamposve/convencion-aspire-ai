namespace Convenciones.Ai;

/// <summary>
/// The price book used to turn reported token usage into an internal cost estimate.
/// </summary>
/// <remarks>
/// Prices are configuration, not a provider contract: the values are whatever the deployment is
/// configured with, and <see cref="Version"/> is recorded next to every cost row so a historical amount
/// can always be explained by the price list that produced it. Increasing a price requires a new
/// version, never an edit of the old one.
/// </remarks>
public sealed record AiPricing(
    string Version,
    string Currency,
    decimal InputPerMillion,
    decimal OutputPerMillion)
{
    /// <summary>
    /// Cost in <see cref="Currency"/>, or <c>null</c> when the provider did not report both token
    /// counts. A missing count is never treated as zero: that would present an unknown charge as free.
    /// </summary>
    public decimal? EstimateCost(int? inputTokens, int? outputTokens)
    {
        if (inputTokens is null || outputTokens is null)
        {
            return null;
        }

        var cost = (inputTokens.Value / 1_000_000m * InputPerMillion)
            + (outputTokens.Value / 1_000_000m * OutputPerMillion);

        return Math.Round(cost, 6, MidpointRounding.AwayFromZero);
    }

    public static AiPricing FromConfiguration(string version, double inputPerMillion, double outputPerMillion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return new AiPricing(
            version,
            "USD",
            (decimal)inputPerMillion,
            (decimal)outputPerMillion);
    }
}
