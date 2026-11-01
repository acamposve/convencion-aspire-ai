namespace Convenciones.Worker.Extraction;

public sealed class ExtractionOptions
{
    public int ChunkCharacters { get; set; } = 10_000;

    public int OverlapCharacters { get; set; } = 1_000;

    public int MaxChunks { get; set; } = 30;

    public int MaxOutputTokens { get; set; } = 8_000;

    public string PromptVersion { get; set; } = "extraction/v1";

    /// <summary>Optional deployment override; the client uses its configured deployment by default.</summary>
    public string? Deployment { get; set; }

    /// <summary>Planning price, USD per 1M input tokens; configurable, not a contract.</summary>
    public double InputPricePerMillion { get; set; } = 0.61;

    public double OutputPricePerMillion { get; set; } = 1.82;
}

public sealed record ClauseChunk(int Index, int PageFrom, int PageTo, string Text, int CharStart);
