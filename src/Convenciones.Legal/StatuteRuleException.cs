namespace Convenciones.Legal;

public enum StatuteRuleKind
{
    Validation,
    Conflict,
    InvalidState,
}

/// <summary>
/// A rule of the legal-library aggregate was violated.
/// </summary>
/// <remarks>
/// The aggregate throws and the endpoint translates, so the domain depends on neither ASP.NET nor
/// HTTP status codes, and every rule can be exercised in a unit test without a server. Mirrors
/// <c>AgreementRuleException</c>.
/// </remarks>
public sealed class StatuteRuleException(StatuteRuleKind kind, string code, string message)
    : Exception(message)
{
    public StatuteRuleKind Kind { get; } = kind;

    public string Code { get; } = code;

    public static StatuteRuleException Validation(string message) =>
        new(StatuteRuleKind.Validation, "validation_failed", message);

    public static StatuteRuleException Conflict(string code, string message) =>
        new(StatuteRuleKind.Conflict, code, message);

    public static StatuteRuleException InvalidState(string message) =>
        new(StatuteRuleKind.InvalidState, "invalid_state", message);
}
