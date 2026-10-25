using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Catalog;

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_entry", "platform");
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id).HasColumnName("id");
        builder.Property(entry => entry.OccurredAt).HasColumnName("occurred_at");
        builder.Property(entry => entry.ActorId).HasColumnName("actor_id");
        builder.Property(entry => entry.Module).HasColumnName("module").IsRequired();
        builder.Property(entry => entry.EntityType).HasColumnName("entity_type").IsRequired();
        builder.Property(entry => entry.EntityId).HasColumnName("entity_id").IsRequired();
        builder.Property(entry => entry.Action).HasColumnName("action").IsRequired();
        builder.Property(entry => entry.Changes).HasColumnName("changes").HasColumnType("jsonb");

        // State transitions are recorded in their own columns: the security model asks for
        // "actor, from -> to" on open/close/reopen/promote and on petition outcome changes.
        builder.Property(entry => entry.FromState).HasColumnName("from_state");
        builder.Property(entry => entry.ToState).HasColumnName("to_state");
        builder.Property(entry => entry.CorrelationId).HasColumnName("correlation_id");

        // The table defaults occurred_at to now(); letting the database keep ownership of the clock
        // means an audit row cannot be backdated by a client or a misconfigured host.
        builder.Property(entry => entry.OccurredAt).HasDefaultValueSql("now()");
    }
}
