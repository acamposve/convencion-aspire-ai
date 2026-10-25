using Microsoft.EntityFrameworkCore;

namespace Convenciones.Negotiation;

/// <summary>Paging bounds for the negotiation list, in one place.</summary>
public static class NegotiationPaging
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;
}

public sealed record ListCasesQuery(CaseState? State, int Page, int PageSize);

public sealed record GetCaseQuery(Guid CaseId);

/// <summary>
/// The read side of the negotiation module: one handler type per use case, projecting with
/// <c>AsNoTracking</c>.
/// </summary>
/// <remarks>
/// The tenant rule arrives as <c>visibleCompanyId</c> (null only for <c>company.read_all</c>) and is
/// applied in the query, not afterwards, so an unfiltered read cannot happen by omission.
/// </remarks>
public sealed class ListCasesHandler
{
    public async Task<CaseListResponse> HandleAsync(
        IQueryable<NegotiationCase> cases,
        Guid? visibleCompanyId,
        ListCasesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(query);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > NegotiationPaging.MaxPageSize
            ? NegotiationPaging.DefaultPageSize
            : query.PageSize;

        var filtered = cases.AsNoTracking();

        if (visibleCompanyId is { } companyId)
        {
            filtered = filtered.Where(negotiationCase => negotiationCase.CompanyId == companyId);
        }

        if (query.State is { } state)
        {
            filtered = filtered.Where(negotiationCase => negotiationCase.State == state);
        }

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await filtered
            .OrderByDescending(negotiationCase => negotiationCase.StartedOn)
            .ThenByDescending(negotiationCase => negotiationCase.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(negotiationCase => new CaseSummaryRow(
                negotiationCase.Id,
                negotiationCase.CompanyId,
                negotiationCase.StartedOn,
                negotiationCase.State,
                negotiationCase.ClosedAt,
                negotiationCase.PromotedAgreementId,
                negotiationCase.Parties.Count,
                negotiationCase.Petitions.Count,
                negotiationCase.Meetings.Count,
                negotiationCase.Petitions.SelectMany(petition => petition.Agreements).Count()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CaseListResponse(
            rows.Select(NegotiationResponses.Summary).ToArray(),
            page,
            pageSize,
            total);
    }
}

public sealed class GetCaseHandler
{
    public async Task<CaseDetail?> HandleAsync(
        IQueryable<NegotiationCase> cases,
        Guid? visibleCompanyId,
        GetCaseQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(query);

        var negotiationCase = await cases
            .AsNoTracking()
            .Include(entity => entity.Parties)
            .Include(entity => entity.Meetings)
            .Include(entity => entity.Petitions)
                .ThenInclude(petition => petition.Offers)
            .Include(entity => entity.Petitions)
                .ThenInclude(petition => petition.Agreements)
            .Where(entity => entity.Id == query.CaseId)
            .Where(entity => visibleCompanyId == null || entity.CompanyId == visibleCompanyId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return negotiationCase is null ? null : NegotiationResponses.Detail(negotiationCase);
    }
}
