using Convenciones.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// The single EF Core context for the whole modular monolith (ADR-003 amended by Task 5: one
/// context, two PostgreSQL schemas — <c>app</c> for business data, <c>platform</c> for
/// infrastructure).
/// </summary>
/// <remarks>
/// Mappings are contributed by the modules that own them, through <c>IEntityTypeConfiguration</c> in
/// each module assembly. Nothing is mapped ahead of a use case: an unused mapping is an untested
/// mapping, and a wrong mapping is worse than a missing one because it fails at runtime rather than
/// at review.
/// </remarks>
public sealed class ConvencionesDbContext(DbContextOptions<ConvencionesDbContext> options)
    : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("app");

        // Catalog owns app.company. Later modules register their own configurations the same way.
        modelBuilder.ApplyConfiguration(new CompanyConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());

        base.OnModelCreating(modelBuilder);
    }
}
