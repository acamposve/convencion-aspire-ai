using Convenciones.Agreements;
using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// The agreement authoring workflow: agreements, clauses, annexes and publication.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/architecture/api-contracts.md §4.3</c> and <c>docs/tasks/task-08-agreements-clauses.md</c>
/// are the contract. The display title is composed here from the company name and the validity
/// period; it is never stored (Q-1).
/// </para>
/// <para>
/// Every route loads the aggregate through <see cref="FindAsync"/> and checks the tenant there.
/// Another company's agreement is 404, never 403: a 403 would confirm that it exists.
/// </para>
/// </remarks>
public static class AgreementEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapAgreementEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/agreements").WithTags("Agreements");

        group.MapGet("/", ListAsync)
            .RequiresCapability("agreement.read")
            .WithName("ListAgreements");

        group.MapPost("/", CreateAsync)
            .RequiresCapability("agreement.write")
            .WithName("CreateAgreement");

        group.MapGet("/{id:guid}", GetAsync)
            .RequiresCapability("agreement.read")
            .WithName("GetAgreement");

        group.MapPatch("/{id:guid}", UpdateAsync)
            .RequiresCapability("agreement.write")
            .WithName("UpdateAgreement");

        group.MapPost("/{id:guid}/publish", PublishAsync)
            .RequiresCapability("agreement.publish")
            .WithName("PublishAgreement");

        group.MapPost("/{id:guid}/unpublish", UnpublishAsync)
            .RequiresCapability("agreement.publish")
            .WithName("UnpublishAgreement");

        group.MapGet("/{id:guid}/clauses", ListClausesAsync)
            .RequiresCapability("agreement.read")
            .WithName("ListAgreementClauses");

        group.MapPost("/{id:guid}/clauses", AddClauseAsync)
            .RequiresCapability("agreement.write")
            .WithName("AddAgreementClause");

        group.MapGet("/{id:guid}/clauses/{clauseId:guid}", GetClauseAsync)
            .RequiresCapability("agreement.read")
            .WithName("GetAgreementClause");

        group.MapPatch("/{id:guid}/clauses/{clauseId:guid}", UpdateClauseAsync)
            .RequiresCapability("agreement.write")
            .WithName("UpdateAgreementClause");

        group.MapDelete("/{id:guid}/clauses/{clauseId:guid}", DeleteClauseAsync)
            .RequiresCapability("agreement.write")
            .WithName("DeleteAgreementClause");

        group.MapPut("/{id:guid}/clauses/{clauseId:guid}/heading", ReclassifyClauseAsync)
            .RequiresCapability("agreement.write")
            .WithName("ReclassifyAgreementClause");

        group.MapGet("/{id:guid}/annexes", ListAnnexesAsync)
            .RequiresCapability("agreement.read")
            .WithName("ListAgreementAnnexes");

        group.MapPost("/{id:guid}/annexes", AddAnnexAsync)
            .RequiresCapability("agreement.write")
            .WithName("AddAgreementAnnex");

        group.MapPatch("/{id:guid}/annexes/{annexId:guid}", UpdateAnnexAsync)
            .RequiresCapability("agreement.write")
            .WithName("UpdateAgreementAnnex");

        group.MapDelete("/{id:guid}/annexes/{annexId:guid}", DeleteAnnexAsync)
            .RequiresCapability("agreement.write")
            .WithName("DeleteAgreementAnnex");

        return app;
    }

    // ---- Agreements -----------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken,
        Guid? companyId = null,
        string? state = null,
        string? q = null,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;

        PublicationState? stateFilter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<PublicationState>(state.Trim(), ignoreCase: true, out var parsed))
            {
                return Validation("State must be 'Draft' or 'Published'.");
            }

            stateFilter = parsed;
        }

        var query = from agreement in db.Agreements.AsNoTracking()
                    join company in db.Companies.AsNoTracking() on agreement.CompanyId equals company.Id
                    select new { Agreement = agreement, CompanyName = company.Name };

        if (!session.HasCapability(TenantScope.CrossCompanyCapability))
        {
            if (session.CompanyId is not { } own)
            {
                return Results.Ok(new AgreementListResponse([], page, pageSize, 0));
            }

            if (companyId is { } filtered && filtered != own)
            {
                return Results.Ok(new AgreementListResponse([], page, pageSize, 0));
            }

            query = query.Where(row => row.Agreement.CompanyId == own);
        }
        else if (companyId is { } anyCompany)
        {
            query = query.Where(row => row.Agreement.CompanyId == anyCompany);
        }

        if (stateFilter is { } wanted)
        {
            query = query.Where(row => row.Agreement.State == wanted);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(row =>
                EF.Functions.ILike(row.CompanyName, term)
                || EF.Functions.ILike(row.Agreement.Scope, term));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await query
            .OrderByDescending(row => row.Agreement.ValidFrom)
            .ThenByDescending(row => row.Agreement.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => new
            {
                row.Agreement,
                row.CompanyName,
                ClauseCount = row.Agreement.Clauses.Count,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows.Select(row => ToSummary(row.Agreement, row.CompanyName, row.ClauseCount)).ToArray();

        return Results.Ok(new AgreementListResponse(items, page, pageSize, total));
    }

    private static async Task<IResult> CreateAsync(
        CreateAgreementRequest request,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var companyId = request.CompanyId ?? session.CompanyId;
        if (companyId is not { } targetCompany)
        {
            return InvalidState("An agreement must belong to a company, and this account has none.");
        }

        if (!TenantScope.CanAccess(session, targetCompany))
        {
            return Forbidden("You cannot create an agreement for another company.");
        }

        var companyName = await db.Companies
            .AsNoTracking()
            .Where(company => company.Id == targetCompany)
            .Select(company => company.Name)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (companyName is null)
        {
            return Validation("The company does not exist.");
        }

        Agreement agreement;

        try
        {
            agreement = Agreement.Create(
                targetCompany,
                request.ValidFrom,
                request.ValidTo,
                request.Scope,
                session.UserId,
                DateTimeOffset.UtcNow);
        }
        catch (AgreementRuleException rule)
        {
            return FromRule(rule);
        }

        db.Agreements.Add(agreement);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, "agreement_company_valid_from_key"))
        {
            return Conflict("An agreement for this company and start date already exists.");
        }

        return Results.Created(
            new Uri($"/agreements/{agreement.Id}", UriKind.Relative),
            ToDetail(agreement, companyName, new Dictionary<Guid, string>()));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        return Results.Ok(await ToDetailAsync(db, agreement, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateAgreementRequest request,
        HttpRequest httpRequest,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        if (!TryReadIfMatch(httpRequest, out var expectedVersion))
        {
            return Validation(
                "The If-Match header with the current rowVersion is required for a partial update.");
        }

        if (agreement.RowVersion != expectedVersion)
        {
            return ConcurrentModification();
        }

        db.Entry(agreement).Property(entity => entity.RowVersion).OriginalValue = expectedVersion;

        try
        {
            agreement.UpdateHeader(
                request.ValidFrom,
                request.ValidTo,
                request.Scope,
                session.UserId,
                DateTimeOffset.UtcNow);
        }
        catch (AgreementRuleException rule)
        {
            return FromRule(rule);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification();
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, "agreement_company_valid_from_key"))
        {
            return Conflict("An agreement for this company and start date already exists.");
        }

        return Results.Ok(await ToDetailAsync(db, agreement, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> PublishAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        try
        {
            agreement.Publish(session.UserId, DateTimeOffset.UtcNow);
        }
        catch (AgreementRuleException rule)
        {
            return FromRule(rule);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification();
        }

        return Results.Ok(await ToDetailAsync(db, agreement, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> UnpublishAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        try
        {
            agreement.Unpublish(session.UserId, DateTimeOffset.UtcNow);
        }
        catch (AgreementRuleException rule)
        {
            return FromRule(rule);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification();
        }

        return Results.Ok(await ToDetailAsync(db, agreement, cancellationToken).ConfigureAwait(false));
    }

    // ---- Clauses --------------------------------------------------------------------------------

    private static async Task<IResult> ListClausesAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        var headings = await HeadingNamesAsync(db, agreement.Clauses, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ClauseListResponse(
            agreement.Clauses
                .OrderBy(clause => clause.Number)
                .Select(clause => ToClause(clause, headings))
                .ToArray(),
            agreement.Clauses.Count));
    }

    private static async Task<IResult> AddClauseAsync(
        Guid id,
        ClauseRequest request,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        var headingName = await RequireHeadingAsync(db, request.HeadingId, cancellationToken)
            .ConfigureAwait(false);

        if (headingName is null)
        {
            return Validation("The heading does not exist or is not active.");
        }

        AgreementClause clause;

        try
        {
            clause = agreement.AddClause(
                request.Number,
                request.Title,
                request.Summary,
                request.Body,
                request.HeadingId,
                request.EconomicComparisonNote,
                request.IsExcludedFromCompilation,
                request.ExclusionNote,
                session.UserId,
                DateTimeOffset.UtcNow);
        }
        catch (AgreementRuleException rule)
        {
            return FromRule(rule);
        }

        // The entity has its key already; adding it through the navigation alone would make EF treat
        // it as an existing row and issue an UPDATE. It is a new clause, so say so explicitly.
        db.AgreementClauses.Add(clause);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, "agreement_clause_number_key"))
        {
            return Conflict($"Clause number {request.Number} already exists in this agreement.");
        }

        return Results.Created(
            new Uri($"/agreements/{agreement.Id}/clauses/{clause.Id}", UriKind.Relative),
            ToClause(clause, new Dictionary<Guid, string> { [request.HeadingId] = headingName }));
    }

    private static async Task<IResult> GetClauseAsync(
        Guid id,
        Guid clauseId,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        var clause = agreement.FindClause(clauseId);
        if (clause is null)
        {
            return NotFoundClause(clauseId);
        }

        var headings = await HeadingNamesAsync(db, [clause], cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToClause(clause, headings));
    }

    private static async Task<IResult> UpdateClauseAsync(
        Guid id,
        Guid clauseId,
        ClauseRequest request,
        HttpRequest httpRequest,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        var clause = agreement.FindClause(clauseId);
        if (clause is null)
        {
            return NotFoundClause(clauseId);
        }

        if (!TryReadIfMatch(httpRequest, out var expectedVersion))
        {
            return Validation(
                "The If-Match header with the current rowVersion is required for a partial update.");
        }

        if (clause.RowVersion != expectedVersion)
        {
            return ConcurrentModification();
        }

        db.Entry(clause).Property(entity => entity.RowVersion).OriginalValue = expectedVersion;

        var headingName = await RequireHeadingAsync(db, request.HeadingId, cancellationToken)
            .ConfigureAwait(false);

        if (headingName is null)
        {
            return Validation("The heading does not exist or is not active.");
        }

        try
        {
            agreement.EnsureNumberAvailable(request.Number, clause.Id);
            clause.Update(
                request.Number,
                request.Title,
                request.Summary,
                request.Body,
                request.HeadingId,
                request.EconomicComparisonNote,
                request.IsExcludedFromCompilation,
                request.ExclusionNote,
                session.UserId,
                DateTimeOffset.UtcNow);
        }
        catch (AgreementRuleException rule)
        {
            return FromRule(rule);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification();
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, "agreement_clause_number_key"))
        {
            return Conflict($"Clause number {request.Number} already exists in this agreement.");
        }

        return Results.Ok(ToClause(
            clause,
            new Dictionary<Guid, string> { [request.HeadingId] = headingName }));
    }

    private static async Task<IResult> DeleteClauseAsync(
        Guid id,
        Guid clauseId,
        HttpRequest httpRequest,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        var clause = agreement.FindClause(clauseId);
        if (clause is null)
        {
            return NotFoundClause(clauseId);
        }

        if (!TryReadIfMatch(httpRequest, out var expectedVersion))
        {
            return Validation("The If-Match header with the current rowVersion is required.");
        }

        if (clause.RowVersion != expectedVersion)
        {
            return ConcurrentModification();
        }

        db.Entry(clause).Property(entity => entity.RowVersion).OriginalValue = expectedVersion;

        try
        {
            agreement.RemoveClause(clause);
        }
        catch (AgreementRuleException rule)
        {
            return FromRule(rule);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification();
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ReclassifyClauseAsync(
        Guid id,
        Guid clauseId,
        ReclassifyClauseRequest request,
        HttpRequest httpRequest,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        var clause = agreement.FindClause(clauseId);
        if (clause is null)
        {
            return NotFoundClause(clauseId);
        }

        if (!TryReadIfMatch(httpRequest, out var expectedVersion))
        {
            return Validation("The If-Match header with the current rowVersion is required.");
        }

        if (clause.RowVersion != expectedVersion)
        {
            return ConcurrentModification();
        }

        var headingName = await RequireHeadingAsync(db, request.HeadingId, cancellationToken)
            .ConfigureAwait(false);

        if (headingName is null)
        {
            return Validation("The heading does not exist or is not active.");
        }

        db.Entry(clause).Property(entity => entity.RowVersion).OriginalValue = expectedVersion;

        try
        {
            clause.Reclassify(request.HeadingId, session.UserId, DateTimeOffset.UtcNow);
        }
        catch (AgreementRuleException rule)
        {
            return FromRule(rule);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification();
        }

        return Results.Ok(ToClause(
            clause,
            new Dictionary<Guid, string> { [request.HeadingId] = headingName }));
    }

    // ---- Annexes --------------------------------------------------------------------------------

    private static async Task<IResult> ListAnnexesAsync(
        Guid id,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        return Results.Ok(new AnnexListResponse(
            agreement.Annexes
                .OrderBy(annex => annex.SortOrder)
                .ThenBy(annex => annex.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToAnnex)
                .ToArray(),
            agreement.Annexes.Count));
    }

    private static async Task<IResult> AddAnnexAsync(
        Guid id,
        AnnexRequest request,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        if (!await DocumentIsAvailableAsync(db, agreement.CompanyId, request.DocumentId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Validation("The referenced document does not exist for this company.");
        }

        AgreementAnnex annex;

        try
        {
            agreement.EnsureAnnexNameAvailable(request.Name, exceptAnnexId: null);
            annex = agreement.AddAnnex(
                request.Name,
                request.Description,
                request.Body,
                request.DocumentId,
                request.SortOrder,
                session.UserId,
                DateTimeOffset.UtcNow);
        }
        catch (AgreementRuleException rule)
        {
            return FromRule(rule);
        }

        db.AgreementAnnexes.Add(annex);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, "agreement_annex_name_key"))
        {
            return Conflict("An annex with that name already exists in this agreement.");
        }

        return Results.Created(
            new Uri($"/agreements/{agreement.Id}/annexes/{annex.Id}", UriKind.Relative),
            ToAnnex(annex));
    }

    private static async Task<IResult> UpdateAnnexAsync(
        Guid id,
        Guid annexId,
        AnnexRequest request,
        HttpRequest httpRequest,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        var annex = agreement.FindAnnex(annexId);
        if (annex is null)
        {
            return NotFoundAnnex(annexId);
        }

        if (!TryReadIfMatch(httpRequest, out var expectedVersion))
        {
            return Validation("The If-Match header with the current rowVersion is required.");
        }

        if (annex.RowVersion != expectedVersion)
        {
            return ConcurrentModification();
        }

        if (!await DocumentIsAvailableAsync(db, agreement.CompanyId, request.DocumentId, cancellationToken)
                .ConfigureAwait(false))
        {
            return Validation("The referenced document does not exist for this company.");
        }

        db.Entry(annex).Property(entity => entity.RowVersion).OriginalValue = expectedVersion;

        try
        {
            agreement.EnsureAnnexNameAvailable(request.Name, annex.Id);
            annex.Update(
                request.Name,
                request.Description,
                request.Body,
                request.DocumentId,
                request.SortOrder,
                session.UserId,
                DateTimeOffset.UtcNow);
        }
        catch (AgreementRuleException rule)
        {
            return FromRule(rule);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification();
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, "agreement_annex_name_key"))
        {
            return Conflict("An annex with that name already exists in this agreement.");
        }

        return Results.Ok(ToAnnex(annex));
    }

    private static async Task<IResult> DeleteAnnexAsync(
        Guid id,
        Guid annexId,
        HttpRequest httpRequest,
        ConvencionesDbContext db,
        ISessionAccessor accessor,
        CancellationToken cancellationToken)
    {
        var session = accessor.Session;
        if (session is null)
        {
            return Forbidden("No session was resolved for this request.");
        }

        var agreement = await FindAsync(db, id, includeChildren: true, cancellationToken)
            .ConfigureAwait(false);

        if (agreement is null || !TenantScope.CanAccess(session, agreement.CompanyId))
        {
            return NotFoundAgreement(id);
        }

        var annex = agreement.FindAnnex(annexId);
        if (annex is null)
        {
            return NotFoundAnnex(annexId);
        }

        if (!TryReadIfMatch(httpRequest, out var expectedVersion))
        {
            return Validation("The If-Match header with the current rowVersion is required.");
        }

        if (annex.RowVersion != expectedVersion)
        {
            return ConcurrentModification();
        }

        db.Entry(annex).Property(entity => entity.RowVersion).OriginalValue = expectedVersion;
        agreement.RemoveAnnex(annex);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentModification();
        }

        return Results.NoContent();
    }

    // ---- Loading and projection -----------------------------------------------------------------

    private static Task<Agreement?> FindAsync(
        ConvencionesDbContext db,
        Guid id,
        bool includeChildren,
        CancellationToken cancellationToken)
    {
        IQueryable<Agreement> query = db.Agreements;

        if (includeChildren)
        {
            query = query
                .Include(agreement => agreement.Clauses)
                .Include(agreement => agreement.Annexes);
        }

        return query.SingleOrDefaultAsync(agreement => agreement.Id == id, cancellationToken);
    }

    private static async Task<AgreementDetail> ToDetailAsync(
        ConvencionesDbContext db,
        Agreement agreement,
        CancellationToken cancellationToken)
    {
        var companyName = await db.Companies
            .AsNoTracking()
            .Where(company => company.Id == agreement.CompanyId)
            .Select(company => company.Name)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? "Empresa";

        var headings = await HeadingNamesAsync(db, agreement.Clauses, cancellationToken)
            .ConfigureAwait(false);

        return ToDetail(agreement, companyName, headings);
    }

    private static AgreementDetail ToDetail(
        Agreement agreement,
        string companyName,
        IReadOnlyDictionary<Guid, string> headings) =>
        new(
            agreement.Id,
            agreement.CompanyId,
            companyName,
            agreement.ComposeTitle(companyName),
            agreement.ValidFrom,
            agreement.ValidTo,
            agreement.Scope,
            agreement.State.ToString(),
            agreement.PublishedAt,
            agreement.CreatedAt,
            agreement.UpdatedAt,
            agreement.RowVersion,
            agreement.Clauses
                .OrderBy(clause => clause.Number)
                .Select(clause => ToClause(clause, headings))
                .ToArray(),
            agreement.Annexes
                .OrderBy(annex => annex.SortOrder)
                .ThenBy(annex => annex.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToAnnex)
                .ToArray());

    private static AgreementSummary ToSummary(Agreement agreement, string companyName, int clauseCount) =>
        new(
            agreement.Id,
            agreement.CompanyId,
            companyName,
            agreement.ComposeTitle(companyName),
            agreement.ValidFrom,
            agreement.ValidTo,
            agreement.Scope,
            agreement.State.ToString(),
            agreement.PublishedAt,
            clauseCount,
            agreement.RowVersion);

    private static ClauseResponse ToClause(
        AgreementClause clause,
        IReadOnlyDictionary<Guid, string> headings) =>
        new(
            clause.Id,
            clause.Number,
            clause.Title,
            clause.Summary,
            clause.Body,
            clause.HeadingId,
            headings.TryGetValue(clause.HeadingId, out var headingName) ? headingName : null,
            clause.EconomicComparisonNote,
            clause.IsExcludedFromCompilation,
            clause.ExclusionNote,
            clause.RowVersion);

    private static AnnexResponse ToAnnex(AgreementAnnex annex) =>
        new(
            annex.Id,
            annex.Name,
            annex.Description,
            annex.Body,
            annex.DocumentId,
            annex.SortOrder,
            annex.RowVersion);

    private static async Task<Dictionary<Guid, string>> HeadingNamesAsync(
        ConvencionesDbContext db,
        IEnumerable<AgreementClause> clauses,
        CancellationToken cancellationToken)
    {
        var ids = clauses.Select(clause => clause.HeadingId).Distinct().ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        return await db.ComparisonHeadings
            .AsNoTracking()
            .Where(heading => ids.Contains(heading.Id))
            .ToDictionaryAsync(heading => heading.Id, heading => heading.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<string?> RequireHeadingAsync(
        ConvencionesDbContext db,
        Guid headingId,
        CancellationToken cancellationToken) =>
        db.ComparisonHeadings
            .AsNoTracking()
            .Where(heading => heading.Id == headingId && heading.IsActive)
            .Select(heading => heading.Name)
            .SingleOrDefaultAsync(cancellationToken);

    private static async Task<bool> DocumentIsAvailableAsync(
        ConvencionesDbContext db,
        Guid companyId,
        Guid? documentId,
        CancellationToken cancellationToken)
    {
        if (documentId is not { } id)
        {
            return true;
        }

        return await db.Documents
            .AsNoTracking()
            .AnyAsync(document => document.Id == id && document.CompanyId == companyId, cancellationToken)
            .ConfigureAwait(false);
    }

    // ---- Errors and headers ---------------------------------------------------------------------

    private static bool TryReadIfMatch(HttpRequest request, out uint version)
    {
        version = 0;
        var header = request.Headers.IfMatch.ToString();

        return !string.IsNullOrWhiteSpace(header)
               && uint.TryParse(header.Trim().Trim('"'), out version);
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraint) =>
        exception.InnerException is Npgsql.PostgresException
        {
            SqlState: "23505",
            ConstraintName: var name,
        } && name == constraint;

    private static IResult FromRule(AgreementRuleException rule) =>
        rule.Kind switch
        {
            AgreementRuleKind.Validation => Validation(rule.Message),
            AgreementRuleKind.Conflict => Conflict(rule.Message),
            _ => InvalidState(rule.Message),
        };

    private static IResult Forbidden(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: detail,
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

    private static IResult InvalidState(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Invalid state",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.InvalidState });

    private static IResult ConcurrentModification() =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "The resource was modified by someone else. Re-read it and retry with the current rowVersion.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ConcurrentModification });

    private static IResult NotFoundAgreement(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No agreement with id '{id}' is visible to the caller.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult NotFoundClause(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No clause with id '{id}' exists in this agreement.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult NotFoundAnnex(Guid id) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: $"No annex with id '{id}' exists in this agreement.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });
}

// ---- Contracts ---------------------------------------------------------------------------------

public sealed record CreateAgreementRequest(
    Guid? CompanyId,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string? Scope);

public sealed record UpdateAgreementRequest(
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string? Scope);

public sealed record ClauseRequest(
    int Number,
    string? Title,
    string? Summary,
    string? Body,
    Guid HeadingId,
    string? EconomicComparisonNote,
    bool IsExcludedFromCompilation,
    string? ExclusionNote);

public sealed record ReclassifyClauseRequest(Guid HeadingId);

public sealed record AnnexRequest(
    string? Name,
    string? Description,
    string? Body,
    Guid? DocumentId,
    int SortOrder);

public sealed record AgreementSummary(
    Guid Id,
    Guid CompanyId,
    string CompanyName,
    string Title,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string Scope,
    string State,
    DateTimeOffset? PublishedAt,
    int ClauseCount,
    uint RowVersion);

public sealed record AgreementDetail(
    Guid Id,
    Guid CompanyId,
    string CompanyName,
    string Title,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string Scope,
    string State,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint RowVersion,
    IReadOnlyList<ClauseResponse> Clauses,
    IReadOnlyList<AnnexResponse> Annexes);

public sealed record AgreementListResponse(
    IReadOnlyList<AgreementSummary> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record ClauseResponse(
    Guid Id,
    int Number,
    string Title,
    string? Summary,
    string Body,
    Guid HeadingId,
    string? HeadingName,
    string? EconomicComparisonNote,
    bool IsExcludedFromCompilation,
    string? ExclusionNote,
    uint RowVersion);

public sealed record ClauseListResponse(
    IReadOnlyList<ClauseResponse> Items,
    int Total);

public sealed record AnnexResponse(
    Guid Id,
    string Name,
    string? Description,
    string? Body,
    Guid? DocumentId,
    int SortOrder,
    uint RowVersion);

public sealed record AnnexListResponse(
    IReadOnlyList<AnnexResponse> Items,
    int Total);
