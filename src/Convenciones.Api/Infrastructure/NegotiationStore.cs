using Convenciones.Negotiation;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// The API's implementation of the negotiation module's persistence port.
/// </summary>
/// <remarks>
/// The tenant rule is applied in the query: <paramref name="visibleCompanyId"/> is null only for a
/// caller holding <c>company.read_all</c>. RLS enforces the same rule again, so a missing predicate here
/// would still not leak rows through the API role.
/// </remarks>
public sealed class NegotiationStore(ConvencionesDbContext db) : INegotiationStore
{
    public Task<NegotiationCase?> FindAsync(
        Guid caseId,
        Guid? visibleCompanyId,
        CancellationToken cancellationToken) =>
        db.NegotiationCases
            .Include(entity => entity.Parties)
            .Include(entity => entity.Meetings)
            .Include(entity => entity.Petitions)
                .ThenInclude(petition => petition.Offers)
            .Include(entity => entity.Petitions)
                .ThenInclude(petition => petition.Agreements)
            .SingleOrDefaultAsync(
                entity => entity.Id == caseId
                    && (visibleCompanyId == null || entity.CompanyId == visibleCompanyId),
                cancellationToken);

    public void Add(NegotiationCase negotiationCase) => db.NegotiationCases.Add(negotiationCase);

    public void Add(UnionPetition petition) => db.UnionPetitions.Add(petition);

    public void Add(EmployerOffer offer) => db.EmployerOffers.Add(offer);

    public void Add(NegotiationMeeting meeting) => db.NegotiationMeetings.Add(meeting);

    public void Add(MeetingAgreement agreement) => db.MeetingAgreements.Add(agreement);

    public void Add(NegotiationParty party) => db.NegotiationParties.Add(party);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
