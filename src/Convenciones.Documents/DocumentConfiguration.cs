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

        builder.Property(document => document.CreatedAt).HasColumnName("created_at");
    }
}
