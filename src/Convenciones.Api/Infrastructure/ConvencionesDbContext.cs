using Convenciones.Agreements;
using Convenciones.Catalog;
using Convenciones.Clauses;
using Convenciones.Documents;
using Convenciones.Legal;
using Convenciones.Negotiation;
using Convenciones.Publishing;
using Convenciones.Administration;
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

    // The legal library. Company-independent reference data: no company column, by design.
    public DbSet<Statute> Statutes => Set<Statute>();

    public DbSet<StatuteArticle> StatuteArticles => Set<StatuteArticle>();

    // Negotiation. Company-scoped by the case's company_id.
    public DbSet<NegotiationCase> NegotiationCases => Set<NegotiationCase>();

    public DbSet<NegotiationParty> NegotiationParties => Set<NegotiationParty>();

    public DbSet<UnionPetition> UnionPetitions => Set<UnionPetition>();

    public DbSet<EmployerOffer> EmployerOffers => Set<EmployerOffer>();

    public DbSet<NegotiationMeeting> NegotiationMeetings => Set<NegotiationMeeting>();

    public DbSet<MeetingAgreement> MeetingAgreements => Set<MeetingAgreement>();

    // Publishing: news is global (no company) because published news is public.
    public DbSet<NewsArticle> NewsArticles => Set<NewsArticle>();

    public DbSet<NewsCategory> NewsCategories => Set<NewsCategory>();

    // Clauses: AI processing runs, extracted clauses and the taxonomy.
    public DbSet<ProcessingRun> ProcessingRuns => Set<ProcessingRun>();
    public DbSet<ProcessingAttempt> ProcessingAttempts => Set<ProcessingAttempt>();
    public DbSet<ExtractedClause> ExtractedClauses => Set<ExtractedClause>();
    public DbSet<ClauseCategory> ClauseCategories => Set<ClauseCategory>();
    public DbSet<ClauseClassification> ClauseClassifications => Set<ClauseClassification>();
    public DbSet<CompanyQuota> CompanyQuotas => Set<CompanyQuota>();
    public DbSet<ProcessingUsage> ProcessingUsages => Set<ProcessingUsage>();

    // Administration: identity tables in the platform schema.
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Capability> Capabilities => Set<Capability>();

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

        // Legal owns app.statute and app.statute_article.
        modelBuilder.ApplyConfiguration(new StatuteConfiguration());
        modelBuilder.ApplyConfiguration(new StatuteArticleConfiguration());

        // Negotiation owns app.negotiation_case, its children and the meeting/offer tables.
        modelBuilder.ApplyConfiguration(new NegotiationCaseConfiguration());
        modelBuilder.ApplyConfiguration(new NegotiationPartyConfiguration());
        modelBuilder.ApplyConfiguration(new UnionPetitionConfiguration());
        modelBuilder.ApplyConfiguration(new EmployerOfferConfiguration());
        modelBuilder.ApplyConfiguration(new NegotiationMeetingConfiguration());
        modelBuilder.ApplyConfiguration(new MeetingAgreementConfiguration());

        // Clauses owns the AI processing tables.
        modelBuilder.ApplyConfiguration(new ProcessingRunConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessingAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new ExtractedClauseConfiguration());
        modelBuilder.ApplyConfiguration(new ClauseCategoryConfiguration());
        modelBuilder.ApplyConfiguration(new ClauseClassificationConfiguration());
        modelBuilder.ApplyConfiguration(new CompanyQuotaConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessingUsageConfiguration());

        // Publishing owns app.news_article and app.news_category.
        modelBuilder.ApplyConfiguration(new NewsArticleConfiguration());
        modelBuilder.ApplyConfiguration(new NewsCategoryConfiguration());

        // Administration reads and writes the identity tables; the same RLS policies guard them.
        modelBuilder.ApplyConfiguration(new UserAccountConfiguration());
        modelBuilder.ApplyConfiguration(new UserProfileConfiguration());
        modelBuilder.ApplyConfiguration(new UserRoleConfiguration());
        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new RoleCapabilityConfiguration());
        modelBuilder.ApplyConfiguration(new CapabilityConfiguration());

        base.OnModelCreating(modelBuilder);
    }
}
