namespace Convenciones.Negotiation;

// ---- Requests ------------------------------------------------------------------------------------

public sealed record CreateCaseRequest(Guid? CompanyId, DateOnly StartedOn);

public sealed record PartyRequest(
    string? Role,
    string? Name,
    string? Phone,
    string? Email,
    string? Position);

public sealed record PetitionRequest(int Number, string? Title, string? Body, Guid? HeadingId);

public sealed record PetitionStateRequest(string? State);

public sealed record OfferRequest(int Number, string? Title, string? Body);

public sealed record MeetingRequest(
    DateOnly HeldOn,
    TimeOnly? StartTime,
    string? Duration,
    string? Summary,
    string? Details,
    string? Attendees);

public sealed record MeetingAgreementRequest(
    DateOnly AgreedOn,
    Guid MeetingId,
    int Number,
    string? Title,
    string? Body,
    Guid? HeadingId);

/// <summary>
/// The agreement a closed case is promoted to. The fields are the ones <c>POST /agreements</c> takes,
/// because promotion creates that same aggregate and must obey the same rules (T11 D4). Legacy wrote
/// literal placeholders here ("*Inicio", "*Fin"); those are rejected.
/// </summary>
public sealed record PromoteCaseRequest(DateOnly ValidFrom, DateOnly? ValidTo, string? Scope);

// ---- Responses -----------------------------------------------------------------------------------

public sealed record PartyResponse(
    Guid Id,
    string Role,
    string Name,
    string? Phone,
    string? Email,
    string? Position);

public sealed record OfferResponse(Guid Id, int Number, string Title, string Body);

public sealed record MeetingResponse(
    Guid Id,
    DateOnly HeldOn,
    TimeOnly? StartTime,
    string? Duration,
    string? Summary,
    string? Details,
    string? Attendees);

public sealed record MeetingAgreementResponse(
    Guid Id,
    Guid PetitionId,
    Guid MeetingId,
    DateOnly AgreedOn,
    int Number,
    string Title,
    string Body,
    Guid? HeadingId);

public sealed record PetitionResponse(
    Guid Id,
    int Number,
    string Title,
    string Body,
    Guid? HeadingId,
    string State,
    IReadOnlyList<OfferResponse> Offers,
    IReadOnlyList<MeetingAgreementResponse> Agreements);

public sealed record CaseSummary(
    Guid Id,
    Guid CompanyId,
    DateOnly StartedOn,
    string State,
    DateTimeOffset? ClosedAt,
    Guid? PromotedAgreementId,
    int PartyCount,
    int PetitionCount,
    int MeetingCount,
    int MeetingAgreementCount);

public sealed record CaseDetail(
    Guid Id,
    Guid CompanyId,
    DateOnly StartedOn,
    string State,
    DateTimeOffset? ClosedAt,
    Guid? PromotedAgreementId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PartyResponse> Parties,
    IReadOnlyList<PetitionResponse> Petitions,
    IReadOnlyList<MeetingResponse> Meetings);

public sealed record CaseListResponse(
    IReadOnlyList<CaseSummary> Items,
    int Page,
    int PageSize,
    int Total);

/// <summary>What promotion answers: the agreement it created and the case it closed.</summary>
public sealed record PromoteCaseResponse(Guid AgreementId, Guid CaseId);
