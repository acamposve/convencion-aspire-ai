namespace Convenciones.Legal;

/// <summary>Shared text rules for the legal library.</summary>
internal static class StatuteText
{
    public static string Required(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw StatuteRuleException.Validation(message);
        }

        return value.Trim();
    }

    public static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
