namespace Convenciones.Agreements;

/// <summary>
/// Publication state of a collective agreement, mirroring <c>app.publication_state</c>.
/// </summary>
/// <remarks>
/// Two states only (Q-7). The legacy column carried three spellings for two states and the
/// application matched two of them (DS-15); a native enum makes the third impossible.
/// </remarks>
public enum PublicationState
{
    Draft,
    Published,
}
