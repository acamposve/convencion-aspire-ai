namespace Convenciones.Agreements;

public enum AgreementRuleKind
{
    Validation,
    Conflict,
    InvalidState,
}

/// <summary>
/// A rule of the agreement aggregate was violated.
/// </summary>
/// <remarks>
/// The aggregate throws; the endpoints translate. Keeping the kind separate from HTTP means the
/// domain does not depend on ASP.NET and the same rule can be exercised in a unit test without a
/// server.
/// </remarks>
public sealed class AgreementRuleException(AgreementRuleKind kind, string code, string message)
    : Exception(message)
{
    public AgreementRuleKind Kind { get; } = kind;

    public string Code { get; } = code;

    public static AgreementRuleException Validation(string message) =>
        new(AgreementRuleKind.Validation, "validation_failed", message);

    public static AgreementRuleException Conflict(string code, string message) =>
        new(AgreementRuleKind.Conflict, code, message);

    public static AgreementRuleException InvalidState(string message) =>
        new(AgreementRuleKind.InvalidState, "invalid_state", message);
}
