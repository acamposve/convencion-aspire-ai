using Convenciones.Agreements;
using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Convenciones.Identity;
using Convenciones.Negotiation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// The negotiation workflow: cases, parties, petitions, offers, meetings, meeting agreements, the five
/// state transitions and promotion.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/architecture/api-contracts.md §4.6</c> and <c>docs/tasks/task-11-negotiation.md</c> are the
/// contract. A closed case is read-only; promotion is a separate operation for a closed case and is
/// terminal (A-21, ADR-014).
/// </para>
/// <para>
/// Illegal transitions answer <b>409 <c>invalid_state</c></b> and carry the current state in a
/// <c>state</c> extension, which T11 requires. Another company's case is <b>404</b> on every route:
/// a 403 would confirm that it exists.
/// </para>
/// </remarks>
public static class NegotiationEndpoints
{
    public static IEndpointRouteBuilder MapNegotiationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/negotiation/cases").WithTags("Negotiation");

        group.MapGet("/", ListAsync)
            .RequiresCapability("negotiation.read")
            .WithName("ListNegotiationCases");

        group.MapPost("/", CreateAsync)
            .RequiresCapability("negotiation.write")
            .WithName("CreateNegotiationCase");

        group.MapGet("/{id:guid}", GetAsync)
            .RequiresCapability("negotiation.read")
            .WithName("GetNegotiationCase");

        group.MapPost("/{id:guid}/parties", AddPartyAsync)
            .RequiresCapability("negotiation.write")
            .WithName("AddNegotiationParty");

        group.MapDelete("/{id:guid}/parties/{partyId:guid}", RemovePartyAsync)
            .RequiresCapability("negotiation.write")
            .WithName("RemoveNegotiationParty");

        group.MapPost("/{id:guid}/petitions", AddPetitionAsync)
            .RequiresCapability("negotiation.write")
            .WithName("AddNegotiationPetition");

        group.MapPatch("/{id:guid}/petitions/{petitionId:guid}", UpdatePetitionAsync)
            .RequiresCapability("negotiation.write")
            .WithName("UpdateNegotiationPetition");

        group.MapDelete("/{id:guid}/petitions/{petitionId:guid}", DeletePetitionAsync)
            .RequiresCapability("negotiation.write")
            .WithName("DeleteNegotiationPetition");

        group.MapPut("/{id:guid}/petitions/{petitionId:guid}/state", ChangePetitionStateAsync)
            .RequiresCapability("negotiation.write")
            .WithName("ChangeNegotiationPetitionState");

        group.MapPost("/{id:guid}/petitions/{petitionId:guid}/offers", AddOfferAsync)
            .RequiresCapability("negotiation.write")
            .WithName("AddNegotiationOffer");

        group.MapDelete("/{id:guid}/petitions/{petitionId:guid}/offers/{offerId:guid}", DeleteOfferAsync)
            .RequiresCapability("negotiation.write")
            .WithName("DeleteNegotiationOffer");

        group.MapPost("/{id:guid}/meetings", AddMeetingAsync)
            .RequiresCapability("negotiation.write")
            .WithName("AddNegotiationMeeting");

        group.MapDelete("/{id:guid}/meetings/{meetingId:guid}", DeleteMeetingAsync)
            .RequiresCapability("negotiation.write")
            .WithName("DeleteNegotiationMeeting");

        group.MapPost("/{id:guid}/petitions/{petitionId:guid}/agreements", AddMeetingAgreementAsync)
            .RequiresCapability("negotiation.write")
            .WithName("AddMeetingAgreement");

        group.MapDelete(
                "/{id:guid}/petitions/{petitionId:guid}/agreements/{agreementId:guid}",
                DeleteMeetingAgreementAsync)
            .RequiresCapability("negotiation.write")
            .WithName("DeleteMeetingAgreement");

        // Transitions. `/open` is deliberately absent: a case is created in Open (T11 D2).
        group.MapPost("/{id:guid}/in-progress", StartInProgressAsync)
            .RequiresCapability("negotiation.write")
            .WithName("StartNegotiationCase");

        group.MapPost("/{id:guid}/pending", MarkPendingAsync)
            .RequiresCapability("negotiation.write")
            .WithName("MarkNegotiationCasePending");

        group.MapPost("/{id:guid}/agree", AgreeAsync)
            .RequiresCapability("negotiation.write")
            .WithName("AgreeNegotiationCase");

        group.MapPost("/{id:guid}/close", CloseAsync)
            .RequiresCapability("negotiation.write")
            .WithName("CloseNegotiationCase");

        group.MapPost("/{id:guid}/reopen", ReopenAsync)
            .RequiresCapability("negotiation.write")
            .WithName("ReopenNegotiationCase");

        group.MapPost("/{id:guid}/promote", PromoteAsync)
            .RequiresCapability("negotiation.promote")
            .WithName("PromoteNegotiationCase");

        return app;
    }

    // ---- Cases -----------------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(
        ConvencionesDbContext db,
        ListCasesHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken,
        string? state = null,
        int page = 1,
        int pageSize = NegotiationPaging.DefaultPageSize)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        CaseState? stateFilter = null;

        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!NegotiationEnumParsing.TryParseCaseState(state, out var parsed))
            {
                return Validation(
                    "State must be 'Open', 'InProgress', 'Pending', 'Agreed' or 'Closed'.");
            }

            stateFilter = parsed;
        }

        var list = await handler
            .HandleAsync(
                db.NegotiationCases,
                TenantScope.VisibleCompanyId(session),
                new ListCasesQuery(stateFilter, page, pageSize),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(list);
    }

    private static async Task<IResult> CreateAsync(
        CreateCaseRequest request,
        ConvencionesDbContext db,
        CreateCaseHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        var companyId = request.CompanyId ?? session.CompanyId;

        if (companyId is not { } targetCompany)
        {
            return InvalidState(
                null,
                "A negotiation case must belong to a company, and this account has none.");
        }

        if (!TenantScope.CanAccess(session, targetCompany))
        {
            return Forbidden("You cannot create a negotiation case for another company.");
        }

        var companyExists = await db.Companies
            .AsNoTracking()
            .AnyAsync(company => company.Id == targetCompany, cancellationToken)
            .ConfigureAwait(false);

        if (!companyExists)
        {
            return Validation("The company does not exist.");
        }

        return await ExecuteAsync(
                () => handler.HandleAsync(targetCompany, request, session.UserId, cancellationToken),
                $"/negotiation/cases")
            .ConfigureAwait(false);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ConvencionesDbContext db,
        GetCaseHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden();
        }

        var detail = await handler
            .HandleAsync(
                db.NegotiationCases,
                TenantScope.VisibleCompanyId(session),
                new GetCaseQuery(id),
                cancellationToken)
            .ConfigureAwait(false);

        return detail is null ? NotFoundCase(id) : Results.Ok(detail);
    }

    // ---- Parties ---------------------------------------------------------------------------------

    private static Task<IResult> AddPartyAsync(
        Guid id,
        PartyRequest request,
        AddPartyHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id, request, TenantScope.VisibleCompanyId(session), session.UserId, cancellationToken),
                $"/negotiation/cases/{id}"));

    private static Task<IResult> RemovePartyAsync(
        Guid id,
        Guid partyId,
        RemovePartyHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id, partyId, TenantScope.VisibleCompanyId(session), session.UserId, cancellationToken)));

    // ---- Petitions -------------------------------------------------------------------------------

    private static Task<IResult> AddPetitionAsync(
        Guid id,
        PetitionRequest request,
        AddPetitionHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id, request, TenantScope.VisibleCompanyId(session), session.UserId, cancellationToken),
                $"/negotiation/cases/{id}"));

    private static Task<IResult> UpdatePetitionAsync(
        Guid id,
        Guid petitionId,
        PetitionRequest request,
        UpdatePetitionHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id,
                    petitionId,
                    request,
                    TenantScope.VisibleCompanyId(session),
                    session.UserId,
                    cancellationToken)));

    private static Task<IResult> DeletePetitionAsync(
        Guid id,
        Guid petitionId,
        DeletePetitionHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id, petitionId, TenantScope.VisibleCompanyId(session), session.UserId, cancellationToken)));

    private static Task<IResult> ChangePetitionStateAsync(
        Guid id,
        Guid petitionId,
        PetitionStateRequest request,
        ChangePetitionStateHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id,
                    petitionId,
                    request,
                    TenantScope.VisibleCompanyId(session),
                    session.UserId,
                    cancellationToken)));

    // ---- Offers ----------------------------------------------------------------------------------

    private static Task<IResult> AddOfferAsync(
        Guid id,
        Guid petitionId,
        OfferRequest request,
        AddOfferHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id,
                    petitionId,
                    request,
                    TenantScope.VisibleCompanyId(session),
                    session.UserId,
                    cancellationToken),
                $"/negotiation/cases/{id}"));

    private static Task<IResult> DeleteOfferAsync(
        Guid id,
        Guid petitionId,
        Guid offerId,
        DeleteOfferHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id,
                    petitionId,
                    offerId,
                    TenantScope.VisibleCompanyId(session),
                    session.UserId,
                    cancellationToken)));

    // ---- Meetings and agreements -----------------------------------------------------------------

    private static Task<IResult> AddMeetingAsync(
        Guid id,
        MeetingRequest request,
        AddMeetingHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id, request, TenantScope.VisibleCompanyId(session), session.UserId, cancellationToken),
                $"/negotiation/cases/{id}"));

    private static Task<IResult> DeleteMeetingAsync(
        Guid id,
        Guid meetingId,
        DeleteMeetingHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id, meetingId, TenantScope.VisibleCompanyId(session), session.UserId, cancellationToken)));

    private static Task<IResult> AddMeetingAgreementAsync(
        Guid id,
        Guid petitionId,
        MeetingAgreementRequest request,
        AddMeetingAgreementHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id,
                    petitionId,
                    request,
                    TenantScope.VisibleCompanyId(session),
                    session.UserId,
                    cancellationToken),
                $"/negotiation/cases/{id}"));

    private static Task<IResult> DeleteMeetingAgreementAsync(
        Guid id,
        Guid petitionId,
        Guid agreementId,
        DeleteMeetingAgreementHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id,
                    petitionId,
                    agreementId,
                    TenantScope.VisibleCompanyId(session),
                    session.UserId,
                    cancellationToken)));

    // ---- Transitions -----------------------------------------------------------------------------

    private static Task<IResult> StartInProgressAsync(
        Guid id,
        TransitionCaseHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        TransitionAsync(id, CaseTransition.StartInProgress, handler, accessor, cancellationToken);

    private static Task<IResult> MarkPendingAsync(
        Guid id,
        TransitionCaseHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        TransitionAsync(id, CaseTransition.MarkPending, handler, accessor, cancellationToken);

    private static Task<IResult> AgreeAsync(
        Guid id,
        TransitionCaseHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        TransitionAsync(id, CaseTransition.Agree, handler, accessor, cancellationToken);

    private static Task<IResult> CloseAsync(
        Guid id,
        TransitionCaseHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        TransitionAsync(id, CaseTransition.Close, handler, accessor, cancellationToken);

    private static Task<IResult> ReopenAsync(
        Guid id,
        TransitionCaseHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        TransitionAsync(id, CaseTransition.Reopen, handler, accessor, cancellationToken);

    private static Task<IResult> PromoteAsync(
        Guid id,
        PromoteCaseRequest request,
        PromoteCaseHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id, request, TenantScope.VisibleCompanyId(session), session.UserId, cancellationToken)));

    private static Task<IResult> TransitionAsync(
        Guid id,
        CaseTransition transition,
        TransitionCaseHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(
                    id, transition, TenantScope.VisibleCompanyId(session), session.UserId, cancellationToken)));

    // ---- Translation -----------------------------------------------------------------------------

    private static Task<IResult> WithSession(
        ISessionAccessor accessor,
        Func<SessionContext, Task<IResult>> action) =>
        accessor.Session is { } session ? action(session) : Task.FromResult(Forbidden());

    private static async Task<IResult> ExecuteAsync(
        Func<Task<NegotiationCommandResult>> action,
        string? location = null)
    {
        try
        {
            return Map(await action().ConfigureAwait(false), location);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification(
                "The case was modified by someone else. Re-read it and retry.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return ConflictFromDatabase(exception);
        }
        catch (DbUpdateException exception) when (IsCapabilityViolation(exception))
        {
            return ForbiddenByDatabase(exception);
        }
        catch (DbUpdateException exception) when (IsStateViolation(exception))
        {
            return InvalidStateFromDatabase(exception);
        }
        catch (AgreementRuleException rule)
        {
            // The agreement model refused the promoted header: same 400 as POST /agreements would give.
            return Validation(rule.Message);
        }
    }

    private static IResult Map(NegotiationCommandResult result, string? location)
    {
        var uri = new Uri(location ?? "/negotiation/cases", UriKind.Relative);

        return result.Status switch
        {
            NegotiationCommandStatus.Created when result.Case is not null =>
                Results.Created(uri, result.Case),
            NegotiationCommandStatus.Created when result.Party is not null =>
                Results.Created(uri, result.Party),
            NegotiationCommandStatus.Created when result.Petition is not null =>
                Results.Created(uri, result.Petition),
            NegotiationCommandStatus.Created when result.Offer is not null =>
                Results.Created(uri, result.Offer),
            NegotiationCommandStatus.Created when result.Meeting is not null =>
                Results.Created(uri, result.Meeting),
            NegotiationCommandStatus.Created when result.MeetingAgreement is not null =>
                Results.Created(uri, result.MeetingAgreement),
            NegotiationCommandStatus.Ok when result.AgreementId is { } agreementId =>
                Results.Ok(new PromoteCaseResponse(agreementId, result.Case?.Id ?? Guid.Empty)),
            NegotiationCommandStatus.Ok when result.Case is not null => Results.Ok(result.Case),
            NegotiationCommandStatus.Ok when result.Petition is not null => Results.Ok(result.Petition),
            NegotiationCommandStatus.NoContent => Results.NoContent(),
            NegotiationCommandStatus.NotFound => NotFoundProblem(result.Detail),
            NegotiationCommandStatus.ValidationFailed => Validation(
                result.Detail ?? "The request is invalid."),
            NegotiationCommandStatus.Conflict => Conflict(
                result.Detail ?? "The request conflicts with the current state."),
            NegotiationCommandStatus.InvalidState => InvalidState(result.State, result.Detail),
            _ => ConcurrentModification(result.Detail),
        };
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };

    private static bool IsCapabilityViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "42501" };

    private static bool IsStateViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "22023" };

    private static IResult ConflictFromDatabase(DbUpdateException exception)
    {
        var constraint = (exception.InnerException as PostgresException)?.ConstraintName;

        return constraint switch
        {
            "negotiation_case_promoted_agreement_key" => Conflict(
                "That agreement is already the product of another negotiation case."),
            "union_petition_number_key" => Conflict(
                "That petition number already exists in this case."),
            "employer_offer_number_key" => Conflict(
                "That offer number already exists in this petition."),
            "meeting_agreement_number_key" => Conflict(
                "That meeting agreement number already exists in this case."),
            _ => Conflict($"A unique index refused the write: {constraint}."),
        };
    }

    private static IResult ForbiddenByDatabase(DbUpdateException exception) =>
        Forbidden(MessageOf(exception) ?? "The capability 'negotiation.promote' is required.");

    private static IResult InvalidStateFromDatabase(DbUpdateException exception) =>
        InvalidState(null, MessageOf(exception) ?? "The database refused the state transition.");

    private static string? MessageOf(DbUpdateException exception) =>
        (exception.InnerException as PostgresException)?.MessageText;

    private static IResult Forbidden(string? detail = null) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: detail ?? "No session was resolved for this request.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Forbidden });

    private static IResult Validation(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Validation failed",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ValidationFailed });

    private static IResult Conflict(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Conflict });

    /// <summary>
    /// 409 invalid_state. The current state is part of the body because T11 requires it, and it is
    /// omitted when the refusal did not come from a state check (a database race, for example).
    /// </summary>
    private static IResult InvalidState(CaseState? state, string? detail)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = ErrorCodes.InvalidState };

        if (state is { } current)
        {
            extensions["state"] = current.ToString();
        }

        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Invalid state",
            detail: detail ?? "The operation is not allowed in the current state.",
            extensions: extensions);
    }

    private static IResult ConcurrentModification(string? detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail
                ?? "The resource was modified by someone else. Re-read it and retry.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ConcurrentModification });

    private static IResult NotFoundProblem(string? detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: detail ?? "The resource does not exist.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult NotFoundCase(Guid id) =>
        NotFoundProblem($"No negotiation case with id '{id}' exists.");
}
