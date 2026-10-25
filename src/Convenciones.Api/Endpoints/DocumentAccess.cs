using Convenciones.Api.Infrastructure;
using Convenciones.Catalog;
using Convenciones.Documents;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// The one place that decides whether a caller may see a document.
/// </summary>
/// <remarks>
/// Every document endpoint loads through this helper, so a new read path cannot quietly skip the
/// tenant check. A document with no company, or one belonging to another company, is reported as
/// <see langword="null"/> — the caller turns that into 404, never 403, because 403 would confirm that
/// the document exists.
/// </remarks>
internal static class DocumentAccess
{
    public static async Task<Document?> FindAccessibleAsync(
        SessionContext session,
        Guid documentId,
        ConvencionesDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var document = await db.Documents
            .SingleOrDefaultAsync(candidate => candidate.Id == documentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        return document.CompanyId is { } companyId && TenantScope.CanAccess(session, companyId)
            ? document
            : null;
    }
}
