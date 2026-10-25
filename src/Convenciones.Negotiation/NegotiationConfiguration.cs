using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Negotiation;

/// <summary>
/// Maps the negotiation aggregate onto the six <c>app</c> tables.
/// </summary>
/// <remarks>
/// The columns are mapped one by one because the schema is the contract. <c>xmin</c> is the concurrency
/// token: it is what makes two concurrent transitions (or two promotions) collide at the database
/// instead of both succeeding. The composite foreign keys that tie a meeting agreement to one case are
/// declared in the schema and not re-declared here; EF writes <c>case_id</c> from the aggregate, and the
/// database refuses a mismatch.
/// </remarks>
public sealed class NegotiationCaseConfiguration : IEntityTypeConfiguration<NegotiationCase>
{
    public void Configure(EntityTypeBuilder<NegotiationCase> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("negotiation_case", "app");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.CompanyId).HasColumnName("company_id");
        builder.Property(entity => entity.StartedOn).HasColumnName("started_on");
        builder.Property(entity => entity.State).HasColumnName("state").IsRequired();
        builder.Property(entity => entity.ClosedAt).HasColumnName("closed_at");
        builder.Property(entity => entity.PromotedAgreementId).HasColumnName("promoted_agreement_id");
        builder.Property(entity => entity.CreatedAt).HasColumnName("created_at");
        builder.Property(entity => entity.CreatedBy).HasColumnName("created_by");
        builder.Property(entity => entity.UpdatedAt).HasColumnName("updated_at");
        builder.Property(entity => entity.UpdatedBy).HasColumnName("updated_by");

        builder.Property(entity => entity.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasMany(entity => entity.Parties)
            .WithOne()
            .HasForeignKey(party => party.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(entity => entity.Petitions)
            .WithOne()
            .HasForeignKey(petition => petition.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(entity => entity.Meetings)
            .WithOne()
            .HasForeignKey(meeting => meeting.CaseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class NegotiationPartyConfiguration : IEntityTypeConfiguration<NegotiationParty>
{
    public void Configure(EntityTypeBuilder<NegotiationParty> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("negotiation_party", "app");
        builder.HasKey(party => party.Id);

        builder.Property(party => party.Id).HasColumnName("id");
        builder.Property(party => party.CaseId).HasColumnName("case_id");
        builder.Property(party => party.Role).HasColumnName("role").IsRequired();
        builder.Property(party => party.Name).HasColumnName("name").IsRequired();
        builder.Property(party => party.Phone).HasColumnName("phone");
        builder.Property(party => party.Email).HasColumnName("email");
        builder.Property(party => party.Position).HasColumnName("position");
        builder.Property(party => party.CreatedAt).HasColumnName("created_at");
        builder.Property(party => party.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class UnionPetitionConfiguration : IEntityTypeConfiguration<UnionPetition>
{
    public void Configure(EntityTypeBuilder<UnionPetition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("union_petition", "app");
        builder.HasKey(petition => petition.Id);

        builder.Property(petition => petition.Id).HasColumnName("id");
        builder.Property(petition => petition.CaseId).HasColumnName("case_id");
        builder.Property(petition => petition.Number).HasColumnName("number_int");
        builder.Property(petition => petition.Title).HasColumnName("title").IsRequired();
        builder.Property(petition => petition.Body).HasColumnName("body").IsRequired();
        builder.Property(petition => petition.HeadingId).HasColumnName("heading_id");
        builder.Property(petition => petition.State).HasColumnName("state").IsRequired();
        builder.Property(petition => petition.CreatedAt).HasColumnName("created_at");
        builder.Property(petition => petition.CreatedBy).HasColumnName("created_by");
        builder.Property(petition => petition.UpdatedAt).HasColumnName("updated_at");
        builder.Property(petition => petition.UpdatedBy).HasColumnName("updated_by");

        builder.Property(petition => petition.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasMany(petition => petition.Offers)
            .WithOne()
            .HasForeignKey(offer => offer.PetitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(petition => petition.Agreements)
            .WithOne()
            .HasForeignKey(agreement => agreement.PetitionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EmployerOfferConfiguration : IEntityTypeConfiguration<EmployerOffer>
{
    public void Configure(EntityTypeBuilder<EmployerOffer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("employer_offer", "app");
        builder.HasKey(offer => offer.Id);

        builder.Property(offer => offer.Id).HasColumnName("id");
        builder.Property(offer => offer.PetitionId).HasColumnName("petition_id");
        builder.Property(offer => offer.Number).HasColumnName("number_int");
        builder.Property(offer => offer.Title).HasColumnName("title").IsRequired();
        builder.Property(offer => offer.Body).HasColumnName("body").IsRequired();
        builder.Property(offer => offer.CreatedAt).HasColumnName("created_at");
        builder.Property(offer => offer.CreatedBy).HasColumnName("created_by");
        builder.Property(offer => offer.UpdatedAt).HasColumnName("updated_at");
        builder.Property(offer => offer.UpdatedBy).HasColumnName("updated_by");

        builder.Property(offer => offer.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

public sealed class NegotiationMeetingConfiguration : IEntityTypeConfiguration<NegotiationMeeting>
{
    public void Configure(EntityTypeBuilder<NegotiationMeeting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("negotiation_meeting", "app");
        builder.HasKey(meeting => meeting.Id);

        builder.Property(meeting => meeting.Id).HasColumnName("id");
        builder.Property(meeting => meeting.CaseId).HasColumnName("case_id");
        builder.Property(meeting => meeting.HeldOn).HasColumnName("held_on");
        builder.Property(meeting => meeting.StartTime).HasColumnName("start_time");
        builder.Property(meeting => meeting.Duration).HasColumnName("duration");
        builder.Property(meeting => meeting.Summary).HasColumnName("summary");
        builder.Property(meeting => meeting.Details).HasColumnName("details");
        builder.Property(meeting => meeting.Attendees).HasColumnName("attendees");
        builder.Property(meeting => meeting.CreatedAt).HasColumnName("created_at");
        builder.Property(meeting => meeting.CreatedBy).HasColumnName("created_by");
        builder.Property(meeting => meeting.UpdatedAt).HasColumnName("updated_at");
        builder.Property(meeting => meeting.UpdatedBy).HasColumnName("updated_by");

        builder.Property(meeting => meeting.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}

public sealed class MeetingAgreementConfiguration : IEntityTypeConfiguration<MeetingAgreement>
{
    public void Configure(EntityTypeBuilder<MeetingAgreement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("meeting_agreement", "app");
        builder.HasKey(agreement => agreement.Id);

        builder.Property(agreement => agreement.Id).HasColumnName("id");
        builder.Property(agreement => agreement.CaseId).HasColumnName("case_id");
        builder.Property(agreement => agreement.PetitionId).HasColumnName("petition_id");
        builder.Property(agreement => agreement.MeetingId).HasColumnName("meeting_id");
        builder.Property(agreement => agreement.AgreedOn).HasColumnName("agreed_on");
        builder.Property(agreement => agreement.Number).HasColumnName("number_int");
        builder.Property(agreement => agreement.Title).HasColumnName("title").IsRequired();
        builder.Property(agreement => agreement.Body).HasColumnName("body").IsRequired();
        builder.Property(agreement => agreement.HeadingId).HasColumnName("heading_id");
        builder.Property(agreement => agreement.CreatedAt).HasColumnName("created_at");
        builder.Property(agreement => agreement.CreatedBy).HasColumnName("created_by");
        builder.Property(agreement => agreement.UpdatedAt).HasColumnName("updated_at");
        builder.Property(agreement => agreement.UpdatedBy).HasColumnName("updated_by");

        builder.Property(agreement => agreement.RowVersion)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
