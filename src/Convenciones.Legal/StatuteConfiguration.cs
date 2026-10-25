using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Legal;

/// <summary>
/// Maps the legal library onto <c>app.statute</c> and <c>app.statute_article</c>.
/// </summary>
/// <remarks>
/// The columns are mapped one by one because the schema is the contract: an implicitly mapped column
/// is a column nobody reviewed. <c>xmin</c> is the concurrency token because PostgreSQL maintains it
/// and the schema has no <c>rowversion</c> column.
/// </remarks>
public sealed class StatuteConfiguration : IEntityTypeConfiguration<Statute>
{
    public void Configure(EntityTypeBuilder<Statute> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("statute", "app");
        builder.HasKey(statute => statute.Id);

        builder.Property(statute => statute.Id).HasColumnName("id");

        // app.statute_kind, mapped pass-through in Program.cs: one is 'LabourLaw', the other
        // 'OtherStatute'. No numeric value is ever written.
        builder.Property(statute => statute.Kind).HasColumnName("kind").IsRequired();

        builder.Property(statute => statute.Name).HasColumnName("name").IsRequired();
        builder.Property(statute => statute.PublishedOn).HasColumnName("published_on");
        builder.Property(statute => statute.Source).HasColumnName("source");
        builder.Property(statute => statute.DocumentId).HasColumnName("document_id");
        builder.Property(statute => statute.CreatedAt).HasColumnName("created_at");
        builder.Property(statute => statute.CreatedBy).HasColumnName("created_by");
        builder.Property(statute => statute.UpdatedAt).HasColumnName("updated_at");
        builder.Property(statute => statute.UpdatedBy).HasColumnName("updated_by");

        builder.Property(statute => statute.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasMany(statute => statute.Articles)
            .WithOne()
            .HasForeignKey(article => article.StatuteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StatuteArticleConfiguration : IEntityTypeConfiguration<StatuteArticle>
{
    public void Configure(EntityTypeBuilder<StatuteArticle> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("statute_article", "app");
        builder.HasKey(article => article.Id);

        builder.Property(article => article.Id).HasColumnName("id");
        builder.Property(article => article.StatuteId).HasColumnName("statute_id");
        builder.Property(article => article.Number).HasColumnName("number_int");
        builder.Property(article => article.Title).HasColumnName("title").IsRequired();
        builder.Property(article => article.Summary).HasColumnName("summary");
        builder.Property(article => article.Body).HasColumnName("body").IsRequired();

        // Nullable: approval item A-12. The column really does accept NULL, and 89.6% of the legacy
        // corpus relied on it.
        builder.Property(article => article.HeadingId).HasColumnName("heading_id");

        builder.Property(article => article.EconomicComparisonNote)
            .HasColumnName("economic_comparison_note");
        builder.Property(article => article.CreatedAt).HasColumnName("created_at");
        builder.Property(article => article.CreatedBy).HasColumnName("created_by");
        builder.Property(article => article.UpdatedAt).HasColumnName("updated_at");
        builder.Property(article => article.UpdatedBy).HasColumnName("updated_by");

        builder.Property(article => article.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
