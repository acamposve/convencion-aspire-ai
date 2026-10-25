using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Publishing;

/// <summary>Maps news onto <c>app.news_article</c> and <c>app.news_category</c>.</summary>
public sealed class NewsArticleConfiguration : IEntityTypeConfiguration<NewsArticle>
{
    public void Configure(EntityTypeBuilder<NewsArticle> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("news_article", "app");
        builder.HasKey(article => article.Id);

        builder.Property(article => article.Id).HasColumnName("id");
        builder.Property(article => article.CategoryId).HasColumnName("category_id");
        builder.Property(article => article.Title).HasColumnName("title").IsRequired();
        builder.Property(article => article.Summary).HasColumnName("summary");
        builder.Property(article => article.Body).HasColumnName("body").IsRequired();
        builder.Property(article => article.State).HasColumnName("state").IsRequired();
        builder.Property(article => article.PublishedAt).HasColumnName("published_at");
        builder.Property(article => article.PublishedBy).HasColumnName("published_by");
        builder.Property(article => article.CreatedAt).HasColumnName("created_at");
        builder.Property(article => article.CreatedBy).HasColumnName("created_by");
        builder.Property(article => article.UpdatedAt).HasColumnName("updated_at");
        builder.Property(article => article.UpdatedBy).HasColumnName("updated_by");

        // The two image columns exist in the schema but are not part of the MVP: a public image would
        // need a document download endpoint, which is a disclosure decision nobody has made. They are
        // absent from the entity, so no projection can publish them by accident.
        builder.Property(article => article.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

public sealed class NewsCategoryConfiguration : IEntityTypeConfiguration<NewsCategory>
{
    public void Configure(EntityTypeBuilder<NewsCategory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("news_category", "app");
        builder.HasKey(category => category.Id);

        builder.Property(category => category.Id).HasColumnName("id");
        builder.Property(category => category.Name).HasColumnName("name").IsRequired();
        builder.Property(category => category.Description).HasColumnName("description");
        builder.Property(category => category.SortOrder).HasColumnName("sort_order");
        builder.Property(category => category.IsActive).HasColumnName("is_active");
        builder.Property(category => category.CreatedAt).HasColumnName("created_at");
        builder.Property(category => category.UpdatedAt).HasColumnName("updated_at");
    }
}
