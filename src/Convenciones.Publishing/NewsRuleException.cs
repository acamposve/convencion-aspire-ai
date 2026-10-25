using Convenciones.Catalog;

namespace Convenciones.Publishing;

public enum NewsRuleKind
{
    Validation,
    Conflict,
}

/// <summary>A rule of the publishing module was violated.</summary>
public sealed class NewsRuleException(NewsRuleKind kind, string code, string message) : Exception(message)
{
    public NewsRuleKind Kind { get; } = kind;

    public string Code { get; } = code;

    public static NewsRuleException Validation(string message) =>
        new(NewsRuleKind.Validation, "validation_failed", message);

    public static NewsRuleException Conflict(string code, string message) =>
        new(NewsRuleKind.Conflict, code, message);
}

internal static class NewsText
{
    public static string Required(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw NewsRuleException.Validation(message);
        }

        return value.Trim();
    }

    public static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The body is rich text, so it is sanitised before the required check: a body that was nothing but
    /// a stripped script tag is refused rather than stored empty.
    /// </summary>
    public static string RequireBody(string? html)
    {
        var cleaned = RichText.Clean(html);

        if (cleaned is null)
        {
            throw NewsRuleException.Validation("The article body is required.");
        }

        return cleaned;
    }

    public static string SanitiseBody(string? html) =>
        RichText.Clean(html) ?? throw NewsRuleException.Validation("The article body cannot be blank.");
}
