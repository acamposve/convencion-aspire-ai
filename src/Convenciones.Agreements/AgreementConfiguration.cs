using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Agreements;

public sealed class AgreementConfiguration : IEntityTypeConfiguration<Agreement>
{
    public void Configure(EntityTypeBuilder<Agreement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agreement", "app");
        builder.HasKey(agreement => agreement.Id);

        builder.Property(agreement => agreement.Id).HasColumnName("id");
        builder.Property(agreement => agreement.CompanyId).HasColumnName("company_id");
        builder.Property(agreement => agreement.ValidFrom).HasColumnName("valid_from");
        builder.Property(agreement => agreement.ValidTo).HasColumnName("valid_to");
        builder.Property(agreement => agreement.Scope).HasColumnName("scope").IsRequired();

        // A PostgreSQL enum, mapped through UseNpgsql(...MapEnum...). The label translator is
        // pass-through because the database defines PascalCase.
        builder.Property(agreement => agreement.State).HasColumnName("state");

        builder.Property(agreement => agreement.PublishedAt).HasColumnName("published_at");
        builder.Property(agreement => agreement.PublishedBy).HasColumnName("published_by");
        builder.Property(agreement => agreement.CreatedAt).HasColumnName("created_at");
        builder.Property(agreement => agreement.CreatedBy).HasColumnName("created_by");
        builder.Property(agreement => agreement.UpdatedAt).HasColumnName("updated_at");
        builder.Property(agreement => agreement.UpdatedBy).HasColumnName("updated_by");

        builder.Property(agreement => agreement.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasMany(agreement => agreement.Clauses)
            .WithOne()
            .HasForeignKey(clause => clause.AgreementId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(agreement => agreement.Annexes)
            .WithOne()
            .HasForeignKey(annex => annex.AgreementId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AgreementClauseConfiguration : IEntityTypeConfiguration<AgreementClause>
{
    public void Configure(EntityTypeBuilder<AgreementClause> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agreement_clause", "app");
        builder.HasKey(clause => clause.Id);

        builder.Property(clause => clause.Id).HasColumnName("id");
        builder.Property(clause => clause.AgreementId).HasColumnName("agreement_id");
        builder.Property(clause => clause.Number).HasColumnName("number_int");
        builder.Property(clause => clause.Title).HasColumnName("title").IsRequired();
        builder.Property(clause => clause.Summary).HasColumnName("summary");
        builder.Property(clause => clause.Body).HasColumnName("body").IsRequired();
        builder.Property(clause => clause.HeadingId).HasColumnName("heading_id");
        builder.Property(clause => clause.EconomicComparisonNote).HasColumnName("economic_comparison_note");
        builder.Property(clause => clause.IsExcludedFromCompilation).HasColumnName("is_excluded_from_compilation");
        builder.Property(clause => clause.ExclusionNote).HasColumnName("exclusion_note");
        builder.Property(clause => clause.CreatedAt).HasColumnName("created_at");
        builder.Property(clause => clause.CreatedBy).HasColumnName("created_by");
        builder.Property(clause => clause.UpdatedAt).HasColumnName("updated_at");
        builder.Property(clause => clause.UpdatedBy).HasColumnName("updated_by");

        builder.Property(clause => clause.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

public sealed class AgreementAnnexConfiguration : IEntityTypeConfiguration<AgreementAnnex>
{
    public void Configure(EntityTypeBuilder<AgreementAnnex> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("agreement_annex", "app");
        builder.HasKey(annex => annex.Id);

        builder.Property(annex => annex.Id).HasColumnName("id");
        builder.Property(annex => annex.AgreementId).HasColumnName("agreement_id");
        builder.Property(annex => annex.Name).HasColumnName("name").IsRequired();
        builder.Property(annex => annex.Description).HasColumnName("description");
        builder.Property(annex => annex.Body).HasColumnName("body");
        builder.Property(annex => annex.DocumentId).HasColumnName("document_id");
        builder.Property(annex => annex.SortOrder).HasColumnName("sort_order");
        builder.Property(annex => annex.CreatedAt).HasColumnName("created_at");
        builder.Property(annex => annex.CreatedBy).HasColumnName("created_by");
        builder.Property(annex => annex.UpdatedAt).HasColumnName("updated_at");
        builder.Property(annex => annex.UpdatedBy).HasColumnName("updated_by");

        builder.Property(annex => annex.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

public sealed class ComparisonHeadingGroupConfiguration : IEntityTypeConfiguration<ComparisonHeadingGroup>
{
    public void Configure(EntityTypeBuilder<ComparisonHeadingGroup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("comparison_heading_group", "app");
        builder.HasKey(group => group.Id);

        builder.Property(group => group.Id).HasColumnName("id");
        builder.Property(group => group.Name).HasColumnName("name").IsRequired();
        builder.Property(group => group.Description).HasColumnName("description");
        builder.Property(group => group.RequiresEconomicComparison).HasColumnName("requires_economic_comparison");
        builder.Property(group => group.SortOrder).HasColumnName("sort_order");
        builder.Property(group => group.IsActive).HasColumnName("is_active");

        builder.HasMany(group => group.Headings)
            .WithOne(heading => heading.Group)
            .HasForeignKey(heading => heading.GroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ComparisonHeadingConfiguration : IEntityTypeConfiguration<ComparisonHeading>
{
    public void Configure(EntityTypeBuilder<ComparisonHeading> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("comparison_heading", "app");
        builder.HasKey(heading => heading.Id);

        builder.Property(heading => heading.Id).HasColumnName("id");
        builder.Property(heading => heading.GroupId).HasColumnName("group_id");
        builder.Property(heading => heading.Name).HasColumnName("name").IsRequired();
        builder.Property(heading => heading.Description).HasColumnName("description");
        builder.Property(heading => heading.IsExcluded).HasColumnName("is_excluded");
        builder.Property(heading => heading.SortOrder).HasColumnName("sort_order");
        builder.Property(heading => heading.IsActive).HasColumnName("is_active");
    }
}
