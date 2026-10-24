using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// The single EF Core context for the whole modular monolith (ADR-003 amended by Task 5: one
/// context, two PostgreSQL schemas — <c>app</c> for business data, <c>platform</c> for
/// infrastructure).
/// </summary>
/// <remarks>
/// Deliberately empty for the infrastructure task. Entity mappings arrive with the modules that own
/// them; nothing is mapped ahead of a use case, because an unused mapping is an untested mapping.
/// The context exists now so that connection configuration, readiness probing and the testing
/// harness are proven before domain code depends on them.
/// </remarks>
public sealed class ConvencionesDbContext(DbContextOptions<ConvencionesDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("app");

        base.OnModelCreating(modelBuilder);
    }
}
