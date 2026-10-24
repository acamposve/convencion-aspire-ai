using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Convenciones.Identity;

/// <summary>
/// Fails startup when an endpoint does not declare its security requirements.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole point of the design: security is a decision someone had to make, not a default
/// someone can forget. An endpoint added without a capability requirement would otherwise be
/// protected only by the fallback authentication policy — authenticated, but authorised for
/// everything, which is the most common way an internal API leaks data.
/// </para>
/// <para>
/// Failing at startup rather than returning 500 at runtime means the mistake is found by the person
/// who made it, in the pull request, instead of by whoever notices the hole later.
/// </para>
/// </remarks>
public static class EndpointSecurityValidator
{
    public static void Validate(IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var routed = endpoints.Where(IsRouted).ToArray();

        // A validator that iterates nothing reports success for a pipeline it never inspected.
        if (routed.Length == 0)
        {
            throw new InvalidOperationException(
                "Endpoint security validation found no routed endpoint. The API always exposes at "
                + "least its probes, so an empty set means enumeration failed and this check would "
                + "have passed vacuously.");
        }

        var undeclared = routed
            .Where(endpoint => !IsDeclared(endpoint))
            .Select(Describe)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (undeclared.Length > 0)
        {
            throw new InvalidOperationException(
                "Every endpoint must declare its security requirement. Add [AllowAnonymous] for a "
                + "public endpoint, .RequiresCapability(\"resource.action\") for a protected one, or "
                + ".RequiresActiveSession() for an endpoint any active user may call. Undeclared: "
                + string.Join(", ", undeclared));
        }
    }

    public static bool IsDeclared(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var metadata = endpoint.Metadata;
        return metadata.GetMetadata<IAllowAnonymous>() is not null
            || metadata.GetMetadata<RequiresCapabilityAttribute>() is not null
            || metadata.GetMetadata<RequiresActiveSessionAttribute>() is not null;
    }

    /// <summary>Only routes serve requests; metadata-only endpoints (e.g. internal) are ignored.</summary>
    public static bool IsRouted(Endpoint endpoint) => endpoint is RouteEndpoint;

    private static string Describe(Endpoint endpoint)
    {
        var pattern = endpoint is RouteEndpoint route
            ? route.RoutePattern.RawText
            : endpoint.DisplayName;

        return string.IsNullOrWhiteSpace(pattern) ? "(unnamed endpoint)" : pattern!;
    }
}
