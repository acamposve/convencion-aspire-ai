namespace Convenciones.Negotiation;

/// <summary>
/// What the negotiation module needs from persistence.
/// </summary>
/// <remarks>
/// <para>
/// The tenant rule lives in the signature rather than in every caller: <c>visibleCompanyId</c> is null
/// only for a caller holding <c>company.read_all</c>, and otherwise restricts the lookup to that
/// company. A case from another company is then <see langword="null"/> — indistinguishable from a
/// missing one, exactly like agreements.
/// </para>
/// <para>
/// New children are registered explicitly. They arrive with their key already set, and adding them
/// through the parent's navigation alone would make the provider treat them as existing rows.
/// </para>
/// </remarks>
public interface INegotiationStore
{
    Task<NegotiationCase?> FindAsync(
        Guid caseId,
        Guid? visibleCompanyId,
        CancellationToken cancellationToken);

    void Add(NegotiationCase negotiationCase);

    void Add(UnionPetition petition);

    void Add(EmployerOffer offer);

    void Add(NegotiationMeeting meeting);

    void Add(MeetingAgreement agreement);

    void Add(NegotiationParty party);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Creates the agreement a promotion produces, attached to the same unit of work.
/// </summary>
/// <remarks>
/// A port because the negotiation module may not see the agreement aggregate's persistence (Art. IV.6),
/// and because the agreement must be created from the same rules as <c>POST /agreements</c> rather than
/// a second, divergent copy of them (T11 D4). The implementation attaches the new agreement to the
/// shared <c>DbContext</c> without saving, so the promotion handler's single <c>SaveChanges</c> commits
/// the agreement and the case link in one transaction.
/// </remarks>
public interface IPromotedAgreementFactory
{
    Guid CreateAndAttach(
        Guid companyId,
        DateOnly validFrom,
        DateOnly? validTo,
        string? scope,
        Guid actorId,
        DateTimeOffset now);
}
