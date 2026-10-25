using Convenciones.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Documents;

public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("document", "platform");
        builder.HasKey(document => document.Id);

        builder.Property(document => document.Id).HasColumnName("id");
        builder.Property(document => document.StoragePath).HasColumnName("storage_path").IsRequired();
        builder.Property(document => document.FileName).HasColumnName("file_name").IsRequired();
        builder.Property(document => document.ContentType).HasColumnName("content_type").IsRequired();
        builder.Property(document => document.SizeBytes).HasColumnName("size_bytes");
        builder.Property(document => document.ChecksumSha256).HasColumnName("checksum_sha256");
        builder.Property(document => document.UploadedBy).HasColumnName("uploaded_by");
        builder.Property(document => document.CompanyId).HasColumnName("company_id");

        // A PostgreSQL enum type, mapped through UseNpgsql(...MapEnum...). No HasColumnType: the
        // store type is resolved from the provider's enum registration.
        builder.Property(document => document.ExtractionStatus).HasColumnName("extraction_status");

        builder.Property(document => document.ExtractionAttempts).HasColumnName("extraction_attempts");
        builder.Property(document => document.ExtractionStartedAt).HasColumnName("extraction_started_at");
        builder.Property(document => document.ExtractionCompletedAt).HasColumnName("extraction_completed_at");
        builder.Property(document => document.ExtractionError).HasColumnName("extraction_error");

        builder.Property(document => document.CreatedAt).HasColumnName("created_at");
        builder.Property(document => document.UploadedAt).HasColumnName("uploaded_at");
        builder.Property(document => document.DeletedAt).HasColumnName("deleted_at");

        // xmin is the row's transaction id: maintained by PostgreSQL, no extra column needed. The
        // contract treats rowVersion as opaque and requires it on DELETE through If-Match.
        builder.Property(document => document.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // Soft deletion is the only deletion this table has; filtering here means no read path can
        // forget it, including the extraction endpoints and the list count.
        builder.HasQueryFilter(document => document.DeletedAt == null);
    }
}
