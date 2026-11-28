using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Catalog;

/// <summary>
/// Mappings for the read-only catalogs a company points at. They map existing tables (#138b adds no
/// schema change); the columns are declared so a projection can read them without guessing names.
/// </summary>
public sealed class CompanySectorConfiguration : IEntityTypeConfiguration<CompanySector>
{
    public void Configure(EntityTypeBuilder<CompanySector> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("company_sector");
        builder.HasKey(sector => sector.Id);
        builder.Property(sector => sector.Id).HasColumnName("id");
        builder.Property(sector => sector.Name).HasColumnName("name").IsRequired();
        builder.Property(sector => sector.SortOrder).HasColumnName("sort_order");
        builder.Property(sector => sector.IsActive).HasColumnName("is_active");
    }
}

public sealed class CompanyTypeConfiguration : IEntityTypeConfiguration<CompanyType>
{
    public void Configure(EntityTypeBuilder<CompanyType> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("company_type");
        builder.HasKey(type => type.Id);
        builder.Property(type => type.Id).HasColumnName("id");
        builder.Property(type => type.Name).HasColumnName("name").IsRequired();
        builder.Property(type => type.SortOrder).HasColumnName("sort_order");
        builder.Property(type => type.IsActive).HasColumnName("is_active");
    }
}

public sealed class RegionStateConfiguration : IEntityTypeConfiguration<RegionState>
{
    public void Configure(EntityTypeBuilder<RegionState> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("state");
        builder.HasKey(state => state.Id);
        builder.Property(state => state.Id).HasColumnName("id");
        builder.Property(state => state.CountryId).HasColumnName("country_id");
        builder.Property(state => state.Name).HasColumnName("name").IsRequired();
    }
}
