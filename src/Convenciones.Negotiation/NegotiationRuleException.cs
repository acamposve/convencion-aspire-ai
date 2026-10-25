namespace Convenciones.Negotiation;

public enum NegotiationRuleKind
{
    Validation,
    Conflict,
    InvalidState,
}

/// <summary>A rule of the negotiation aggregate was violated.</summary>
/// <remarks>
/// The aggregate throws and the endpoint translates, so the domain depends on neither ASP.NET nor HTTP
/// status codes. The current state travels with an invalid-state failure because T11 requires the
/// response to name it.
/// </remarks>
public sealed class NegotiationRuleException(
    NegotiationRuleKind kind,
    string code,
    string message,
    CaseState? currentState = null)
    : Exception(message)
{
    public NegotiationRuleKind Kind { get; } = kind;

    public string Code { get; } = code;

    /// <summary>The case state at the time of the refusal, when the failure was a transition.</summary>
    public CaseState? CurrentState { get; } = currentState;

    public static NegotiationRuleException Validation(string message) =>
        new(NegotiationRuleKind.Validation, "validation_failed", message);

    public static NegotiationRuleException Conflict(string code, string message) =>
        new(NegotiationRuleKind.Conflict, code, message);

    public static NegotiationRuleException InvalidState(CaseState state, string message) =>
        new(NegotiationRuleKind.InvalidState, "invalid_state", message, state);
}
