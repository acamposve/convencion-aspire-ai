namespace Convenciones.Worker.Extraction;

public sealed class ExtractionOptions
{
    public int ChunkCharacters { get; set; } = 10_000;

    public int OverlapCharacters { get; set; } = 1_000;

    public int MaxChunks { get; set; } = 30;

    public int MaxOutputTokens { get; set; } = 8_000;

    public string PromptVersion { get; set; } = PromptCatalog.ExtractionVersion;

    /// <summary>Optional deployment override; the client uses its configured deployment by default.</summary>
    public string? Deployment { get; set; }

    /// <summary>Planning price, USD per 1M input tokens; configurable, not a contract.</summary>
    public double InputPricePerMillion { get; set; } = 0.61;

    public double OutputPricePerMillion { get; set; } = 1.82;

    /// <summary>
    /// Identifier of the price list above. Recorded on every usage row so a historical cost can be
    /// explained by the prices that produced it; change it whenever a price changes.
    /// </summary>
    public string PricingVersion { get; set; } = "configured-v1";
}

public sealed record ClauseChunk(int Index, int PageFrom, int PageTo, string Text, int CharStart);
