using System.Diagnostics;
using System.Text.Json;
using Convenciones.Catalog;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Writes one <c>platform.audit_entry</c> row for every change to an <see cref="IAuditable"/> entity,
/// in the same transaction as the change itself.
/// </summary>
/// <remarks>
/// <para>
/// It reads the change tracker rather than being called by handlers, because a handler that forgets
/// leaves a hole that looks exactly like "nothing happened" — and an audit trail with invisible holes
/// is worse than no trail, since it is trusted.
/// </para>
/// <para>
/// Same-transaction is deliberate: a rolled-back operation leaves no audit row, and an audit row never
/// describes a change that did not commit.
/// </para>
/// </remarks>
public sealed class AuditInterceptor(ISessionAccessor accessor, ILogger<AuditInterceptor> logger)
    : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context)
        {
            Record(context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context)
        {
            Record(context);
        }

        return base.SavingChanges(eventData, result);
    }

    private void Record(DbContext context)
    {
        var actorId = accessor.Session?.UserId;
        var correlationId = Activity.Current?.TraceId.ToString();

        var entries = context.ChangeTracker
            .Entries()
            .Where(entry => entry.Entity is IAuditable
                && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToArray();

        foreach (var entry in entries)
        {
            var auditable = (IAuditable)entry.Entity;
            var action = entry.State switch
            {
                EntityState.Added => "created",
                EntityState.Modified => "updated",
                _ => "deleted",
            };

            // An update that changed nothing must not produce a row claiming it changed something.
            if (entry.State is EntityState.Modified && !entry.Properties.Any(p => p.IsModified))
            {
                continue;
            }

            var audit = AuditEntry.Record(
                actorId,
                auditable.AuditModule,
                auditable.AuditEntityType,
                auditable.AuditEntityId,
                action,
                DescribeChanges(entry),
                correlationId);

            context.Add(audit);
        }

        if (entries.Length > 0)
        {
            logger.LogDebug("Recording {Count} audit entr(ies).", entries.Length);
        }
    }

    private static string? DescribeChanges(EntityEntry entry)
    {
        if (entry.State is not EntityState.Modified)
        {
            return null;
        }

        var changed = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in entry.Properties)
        {
            if (!property.IsModified)
            {
                continue;
            }

            changed[property.Metadata.Name] = new
            {
                from = property.OriginalValue,
                to = property.CurrentValue,
            };
        }

        return changed.Count == 0 ? null : JsonSerializer.Serialize(changed);
    }
}
