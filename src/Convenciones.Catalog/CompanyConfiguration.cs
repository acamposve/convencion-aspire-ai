using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Catalog;

public sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("company");
        builder.HasKey(company => company.Id);

        builder.Property(company => company.Id).HasColumnName("id");
        builder.Property(company => company.Name).HasColumnName("name").IsRequired();
        builder.Property(company => company.LegalName).HasColumnName("legal_name");
        builder.Property(company => company.TaxId).HasColumnName("tax_id");
        builder.Property(company => company.LegacyCode).HasColumnName("legacy_code");

        builder.Property(company => company.StreetAddress).HasColumnName("street_address");
        builder.Property(company => company.City).HasColumnName("city");
        builder.Property(company => company.CountryId).HasColumnName("country_id");
        builder.Property(company => company.StateId).HasColumnName("state_id");
        builder.Property(company => company.MunicipalityId).HasColumnName("municipality_id");
        builder.Property(company => company.PostalCode).HasColumnName("postal_code");

        builder.Property(company => company.ContactName).HasColumnName("contact_name");
        builder.Property(company => company.ContactPosition).HasColumnName("contact_position");
        builder.Property(company => company.ContactPhone).HasColumnName("contact_phone");
        builder.Property(company => company.ContactEmail).HasColumnName("contact_email");

        builder.Property(company => company.SectorId).HasColumnName("sector_id");
        builder.Property(company => company.TypeId).HasColumnName("type_id");
        builder.Property(company => company.CategoryId).HasColumnName("category_id");
        builder.Property(company => company.ActivityId).HasColumnName("activity_id");

        builder.Property(company => company.WorkerCount).HasColumnName("worker_count");
        builder.Property(company => company.EmployeeCount).HasColumnName("employee_count");
        builder.Property(company => company.SizeBand).HasColumnName("size_band");

        builder.Property(company => company.LogoDocumentId).HasColumnName("logo_document_id");
        builder.Property(company => company.ArchivedAt).HasColumnName("archived_at");

        builder.Property(company => company.CreatedAt).HasColumnName("created_at");
        builder.Property(company => company.UpdatedAt).HasColumnName("updated_at");

        // xmin is the row's transaction id: monotonic, maintained by PostgreSQL, and requiring no
        // extra column. The schema has no rowversion column and adding one would be a migration for
        // something the database already tracks.
        builder.Property(company => company.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
