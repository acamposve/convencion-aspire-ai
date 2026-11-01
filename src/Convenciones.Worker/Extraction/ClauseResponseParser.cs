using System.Text.Json;

namespace Convenciones.Worker.Extraction;

public sealed record ParsedClause(
    string? Number,
    string? Heading,
    string Text,
    int PageFrom,
    int PageTo,
    double Confidence);

public sealed class ClauseExtractionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ClauseResponseParser
{
    public static IReadOnlyList<ParsedClause> Parse(JsonElement content, ClauseChunk chunk)
    {
        if (!content.TryGetProperty("clauses", out var clauses) || clauses.ValueKind is not JsonValueKind.Array)
        {
            throw new ClauseExtractionException("invalid_model_output", "The model response has no clauses array.");
        }

        var result = new List<ParsedClause>();
        foreach (var clause in clauses.EnumerateArray())
        {
            var text = clause.TryGetProperty("text", out var textElement) ? textElement.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var pageFrom = clause.TryGetProperty("page_from", out var from) && from.TryGetInt32(out var fromValue)
                ? fromValue
                : chunk.PageFrom;
            var pageTo = clause.TryGetProperty("page_to", out var to) && to.TryGetInt32(out var toValue)
                ? toValue
                : chunk.PageTo;

            if (pageFrom < chunk.PageFrom || pageTo > chunk.PageTo || pageFrom > pageTo)
            {
                pageFrom = Math.Clamp(pageFrom, chunk.PageFrom, chunk.PageTo);
                pageTo = Math.Clamp(pageTo, pageFrom, chunk.PageTo);
            }

            var confidence = clause.TryGetProperty("confidence", out var confidenceElement)
                && confidenceElement.TryGetDouble(out var value)
                ? value
                : 0.0;

            if (confidence is < 0 or > 1)
            {
                throw new ClauseExtractionException(
                    "invalid_model_output",
                    "A clause confidence is outside the 0..1 range.");
            }

            result.Add(new ParsedClause(
                ReadOptional(clause, "number"),
                ReadOptional(clause, "heading"),
                text,
                pageFrom,
                pageTo,
                confidence));
        }

        return result;
    }

    private static string? ReadOptional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
