using Convenciones.Agreements;
using Convenciones.Catalog;
using Convenciones.Documents;
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

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentText> DocumentTexts => Set<DocumentText>();

    public DbSet<Agreement> Agreements => Set<Agreement>();

    public DbSet<AgreementClause> AgreementClauses => Set<AgreementClause>();

    public DbSet<AgreementAnnex> AgreementAnnexes => Set<AgreementAnnex>();

    public DbSet<ComparisonHeading> ComparisonHeadings => Set<ComparisonHeading>();

    public DbSet<ComparisonHeadingGroup> ComparisonHeadingGroups => Set<ComparisonHeadingGroup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("app");


        // Catalog owns app.company. Later modules register their own configurations the same way.
        modelBuilder.ApplyConfiguration(new CompanyConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());
        modelBuilder.ApplyConfiguration(new DocumentConfiguration());
        modelBuilder.ApplyConfiguration(new DocumentTextConfiguration());

        // Agreements owns app.agreement and its children, plus the read-only heading taxonomy.
        modelBuilder.ApplyConfiguration(new AgreementConfiguration());
        modelBuilder.ApplyConfiguration(new AgreementClauseConfiguration());
        modelBuilder.ApplyConfiguration(new AgreementAnnexConfiguration());
        modelBuilder.ApplyConfiguration(new ComparisonHeadingGroupConfiguration());
        modelBuilder.ApplyConfiguration(new ComparisonHeadingConfiguration());

        base.OnModelCreating(modelBuilder);
    }
}
