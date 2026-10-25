namespace Convenciones.Catalog;

/// <summary>
/// Marks an entity whose changes must be recorded in <c>platform.audit_entry</c>.
/// </summary>
/// <remarks>
/// Declared on the entity rather than passed at the call site so that a change cannot be saved without
/// a matching audit row: the interceptor works from the change tracker, not from whether somebody
/// remembered.
/// </remarks>
public interface IAuditable
{
    /// <summary>One of the <c>audit_entry_module_allowed</c> values.</summary>
    string AuditModule { get; }

    string AuditEntityType { get; }

    string AuditEntityId { get; }
}
