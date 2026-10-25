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
    /// Published agreements of one company with at least one clause under the heading.
    /// </summary>
    public static IQueryable<ComparisonCandidateRow> Candidates(
        IQueryable<Agreement> agreements,
        IQueryable<Company> companies,
        Guid companyId,
        Guid headingId) =>
        from agreement in agreements
        join company in companies on agreement.CompanyId equals company.Id
        where agreement.CompanyId == companyId
              && agreement.State == PublicationState.Published
              && agreement.Clauses.Any(clause => clause.HeadingId == headingId)
        orderby agreement.ValidFrom descending, agreement.Id
        select new ComparisonCandidateRow(
            agreement.Id,
            agreement.CompanyId,
            company.Name,
            agreement.ValidFrom,
            agreement.ValidTo,
            agreement.Clauses.Count(clause => clause.HeadingId == headingId));
}
