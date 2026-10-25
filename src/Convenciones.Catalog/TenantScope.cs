using Convenciones.Identity;

namespace Convenciones.Catalog;

/// <summary>
/// Decides which company's data a caller may see — in the application, in one place.
/// </summary>
/// <remarks>
/// <para>
/// The database already enforces tenancy with RLS, and that remains the last line of defence. This is
/// the first one: a query written without a company filter is a query that returns another company's
/// rows whenever the policy is broader than expected, and a policy being broader than expected is
/// exactly the mistake the cross-tenant tests were written to catch.
/// </para>
/// <para>
/// The rule is the one the Product Owner chose: membership constrains every user, and
/// <c>company.read_all</c> — granted only to SuperAdmin and Admin — is what unlocks cross-company
/// access.
/// </para>
/// </remarks>
public static class TenantScope
{
    public const string CrossCompanyCapability = "company.read_all";

    public static bool CanAccess(SessionContext session, Guid companyId)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.HasCapability(CrossCompanyCapability)
            || (session.CompanyId is { } own && own == companyId);
    }

    /// <summary>
    /// The company filter for a list query: <see langword="null"/> means unrestricted.
    /// </summary>
    /// <remarks>
    /// Returning a nullable rather than a "predicate that is always true" forces the caller to think
    /// about the unrestricted case instead of inheriting it by default.
    /// </remarks>
    public static Guid? VisibleCompanyId(SessionContext session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.HasCapability(CrossCompanyCapability) ? null : session.CompanyId;
    }
}
