using Convenciones.Agreements;
using Convenciones.Catalog;

namespace Convenciones.Comparison;

/// <summary>
/// Read-only projections for the comparison workspace.
/// </summary>
/// <remarks>
/// The module receives queryables rather than a <c>DbContext</c>, so it stays free of the API's
/// infrastructure and every method is obviously a read. The endpoints apply <c>AsNoTracking</c>; there
/// is no write method here and the routes are GET-only (T9 AC 4).
/// </remarks>
public static class ComparisonQueries
{
    /// <summary>
    /// Published agreements with at least one clause under the heading — the comparison directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #138b widens the scope from "my company" to "every published agreement". A draft of another
    /// company stays invisible: the tenant predicate is not dropped, it is replaced by the publication
    /// predicate, and the row-level policies restrict <c>app_api</c> to <c>Published</c> as well — the
    /// database refuses the read even if this query were wrong.
    /// </para>
    /// <para>
    /// The filters are applied here, before paging, so <c>total</c> means "matching rows" and not
    /// "rows on this page". Filtering in the client would produce a total that lies as soon as the
    /// directory outgrows one page.
    /// </para>
    /// </remarks>
    public static IQueryable<ComparisonCandidateRow> Candidates(
        IQueryable<Agreement> agreements,
        IQueryable<Company> companies,
        IQueryable<CompanySector> sectors,
        IQueryable<CompanyType> types,
        IQueryable<RegionState> states,
        Guid? companyId,
        Guid? sectorId,
        Guid? typeId,
        Guid? stateId,
        Guid headingId) =>
        from agreement in agreements
        join company in companies on agreement.CompanyId equals company.Id
        where agreement.State == PublicationState.Published
              && (companyId == null || agreement.CompanyId == companyId)
              && (sectorId == null || company.SectorId == sectorId)
              && (typeId == null || company.TypeId == typeId)
              && (stateId == null || company.StateId == stateId)
              && agreement.Clauses.Any(clause => clause.HeadingId == headingId)
        orderby agreement.ValidFrom descending, agreement.Id
        select new ComparisonCandidateRow(
            agreement.Id,
            agreement.CompanyId,
            company.Name,
            agreement.ValidFrom,
            agreement.ValidTo,
            agreement.Clauses.Count(clause => clause.HeadingId == headingId),
            // Scalar subqueries rather than three left joins: the catalogs are tiny, and this keeps the
            // projection readable and translatable without a join per optional column.
            company.SectorId,
            sectors.Where(sector => sector.Id == company.SectorId).Select(sector => sector.Name).FirstOrDefault(),
            company.TypeId,
            types.Where(type => type.Id == company.TypeId).Select(type => type.Name).FirstOrDefault(),
            company.StateId,
            states.Where(state => state.Id == company.StateId).Select(state => state.Name).FirstOrDefault());
}
