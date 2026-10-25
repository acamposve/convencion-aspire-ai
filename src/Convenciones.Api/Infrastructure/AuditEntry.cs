namespace Convenciones.Api.Infrastructure;

/// <summary>
/// An append-only record of a change. Mirrors <c>platform.audit_entry</c>.
/// </summary>
/// <remarks>
/// The table rejects UPDATE and DELETE with a trigger, so this type has no setters after construction:
/// the only operation that exists is append.
/// </remarks>
public sealed class AuditEntry
{
    public long Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? ActorId { get; private set; }

    public string Module { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public string EntityId { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public string? Changes { get; private set; }

    /// <summary>State before the change, when the entity has a state machine.</summary>
    public string? FromState { get; private set; }

    /// <summary>State after the change, when the entity has a state machine.</summary>
    public string? ToState { get; private set; }

    public string? CorrelationId { get; private set; }

    public static AuditEntry Record(
        Guid? actorId,
        string module,
        string entityType,
        string entityId,
        string action,
        string? changes,
        string? correlationId,
        string? fromState = null,
        string? toState = null) =>
        new()
        {
            ActorId = actorId,
            Module = module,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Changes = changes,
            FromState = fromState,
            ToState = toState,
            CorrelationId = correlationId,
        };
}
