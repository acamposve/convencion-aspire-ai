using Convenciones.Agreements;
using Convenciones.Negotiation;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Creates the agreement a promotion produces, using the agreement module's own factory.
/// </summary>
/// <remarks>
/// There is deliberately no second implementation of agreement creation: promotion calls
/// <see cref="Agreement.Create"/>, so its date, scope and company rules are exactly those of
/// <c>POST /agreements</c> (T11 D4). The entity is attached to the shared context and not saved here —
/// the promotion handler's single <c>SaveChanges</c> commits it with the case link in one transaction.
/// </remarks>
public sealed class PromotedAgreementFactory(ConvencionesDbContext db) : IPromotedAgreementFactory
{
    public Guid CreateAndAttach(
        Guid companyId,
        DateOnly validFrom,
        DateOnly? validTo,
        string? scope,
        Guid actorId,
        DateTimeOffset now)
    {
        var agreement = Agreement.Create(companyId, validFrom, validTo, scope, actorId, now);
        db.Agreements.Add(agreement);
        return agreement.Id;
    }
}
