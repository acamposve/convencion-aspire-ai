using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Administration;

/// <summary>
/// Maps the identity tables in the <c>platform</c> schema.
/// </summary>
/// <remarks>
/// These are not business tables: no company, no <c>xmin</c> token exposed to a client (there is no
/// <c>If-Match</c> on user administration), and the relationships are one account → one profile → many
/// role grants. RLS on the same tables is the guard; the mapping only has to agree with the columns.
/// </remarks>
public sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_account", "platform");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id).HasColumnName("id");
        builder.Property(user => user.Email).HasColumnName("email").IsRequired();
        builder.Property(user => user.DisplayName).HasColumnName("display_name").IsRequired();
        builder.Property(user => user.IsBlocked).HasColumnName("is_blocked");
        builder.Property(user => user.ExpiresOn).HasColumnName("expires_on");
        builder.Property(user => user.CreatedAt).HasColumnName("created_at");
        builder.Property(user => user.CreatedBy).HasColumnName("created_by");
        builder.Property(user => user.UpdatedAt).HasColumnName("updated_at");
        builder.Property(user => user.UpdatedBy).HasColumnName("updated_by");

        builder.HasOne(user => user.Profile)
            .WithOne()
            .HasForeignKey<UserProfile>(profile => profile.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(user => user.Roles)
            .WithOne()
            .HasForeignKey(assignment => assignment.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_profile", "platform");
        builder.HasKey(profile => profile.UserId);

        builder.Property(profile => profile.UserId).HasColumnName("user_id");
        builder.Property(profile => profile.Phone).HasColumnName("phone");
        builder.Property(profile => profile.CompanyId).HasColumnName("company_id");
        builder.Property(profile => profile.CreatedAt).HasColumnName("created_at");
        builder.Property(profile => profile.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_role", "platform");
        builder.HasKey(assignment => new { assignment.UserId, assignment.RoleId });

        builder.Property(assignment => assignment.UserId).HasColumnName("user_id");
        builder.Property(assignment => assignment.RoleId).HasColumnName("role_id");
        builder.Property(assignment => assignment.GrantedAt).HasColumnName("granted_at");
        builder.Property(assignment => assignment.GrantedBy).HasColumnName("granted_by");

        builder.HasOne(assignment => assignment.Role)
            .WithMany()
            .HasForeignKey(assignment => assignment.RoleId);
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("role", "platform");
        builder.HasKey(role => role.Id);

        builder.Property(role => role.Id).HasColumnName("id");
        builder.Property(role => role.Key).HasColumnName("key").IsRequired();
        builder.Property(role => role.Name).HasColumnName("name").IsRequired();
        builder.Property(role => role.Description).HasColumnName("description");
        builder.Property(role => role.IsSystem).HasColumnName("is_system");

        builder.HasMany(role => role.Capabilities)
            .WithOne()
            .HasForeignKey(grant => grant.RoleId);
    }
}

public sealed class RoleCapabilityConfiguration : IEntityTypeConfiguration<RoleCapability>
{
    public void Configure(EntityTypeBuilder<RoleCapability> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("role_capability", "platform");
        builder.HasKey(grant => new { grant.RoleId, grant.CapabilityId });

        builder.Property(grant => grant.RoleId).HasColumnName("role_id");
        builder.Property(grant => grant.CapabilityId).HasColumnName("capability_id");

        builder.HasOne(grant => grant.Capability)
            .WithMany()
            .HasForeignKey(grant => grant.CapabilityId);
    }
}

public sealed class CapabilityConfiguration : IEntityTypeConfiguration<Capability>
{
    public void Configure(EntityTypeBuilder<Capability> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("capability", "platform");
        builder.HasKey(capability => capability.Id);

        builder.Property(capability => capability.Id).HasColumnName("id");
        builder.Property(capability => capability.Key).HasColumnName("key").IsRequired();
        builder.Property(capability => capability.Description).HasColumnName("description").IsRequired();
    }
}
