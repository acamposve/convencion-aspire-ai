using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Convenciones.Identity;

/// <summary>
/// Declares that an endpoint needs the caller's session to be active. Use it for endpoints that
/// every active user may call, such as <c>/me</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresActiveSessionAttribute : Attribute;

/// <summary>
/// Declares the capability, or capabilities, an endpoint needs. **All** of them are required.
/// </summary>
/// <remarks>
/// <para>
/// Requiring several capabilities is not the same as requiring one of them. Creating a company needs
/// both <c>company.write</c> and <c>company.read_all</c>, because a user who could create a company
/// without cross-company visibility would be granting themselves a new tenant — privilege escalation
/// through a side door.
/// </para>
/// <para>
/// The capability is enforced twice, deliberately: here against the session read from the database,
/// and again by RLS on the table being touched. The API check produces a clear 403; the policy is what
/// protects the data if the API check were ever removed by mistake.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresCapabilityAttribute : Attribute
{
    public RequiresCapabilityAttribute(string capability, params string[] additionalCapabilities)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            throw new ArgumentException("A capability key is required.", nameof(capability));
        }

        var all = new List<string> { capability };
        foreach (var extra in additionalCapabilities)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                all.Add(extra);
            }
        }

        Capabilities = all.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Every one of these must be held.</summary>
    public IReadOnlyList<string> Capabilities { get; }
}

/// <summary>
/// Declares that an endpoint serves public data: it is anonymous, and its database work must run under
/// the <c>anon</c> role so the public RLS policies apply instead of being bypassed by the API login.
/// </summary>
/// <remarks>
/// The marker is separate from <see cref="Microsoft.AspNetCore.Authorization.IAllowAnonymous"/> because
/// "anonymous" alone does not imply "unprivileged": the bootstrap endpoint is anonymous and needs to
/// write, while the public portal is anonymous and must read only what <c>anon</c> can see. Without this,
/// an anonymous request would reset the role and run with the login's privileges — the exact hole a
/// public projection cannot afford.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PublicDataAttribute : Attribute;

public static class EndpointSecurityExtensions
{
    public static TBuilder RequiresCapability<TBuilder>(this TBuilder builder, params string[] capabilities)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (capabilities is null || capabilities.Length == 0)
        {
            throw new ArgumentException("At least one capability is required.", nameof(capabilities));
        }

        return builder.WithMetadata(new RequiresCapabilityAttribute(
            capabilities[0],
            capabilities.Skip(1).ToArray()));
    }

    public static TBuilder RequiresActiveSession<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RequiresActiveSessionAttribute());
    }

    /// <summary>
    /// Marks an anonymous endpoint whose database work must run as <c>anon</c>. Combine it with
    /// <c>AllowAnonymous()</c>: the session middleware sets the flag and the connection interceptor
    /// switches the role, so RLS does the filtering.
    /// </summary>
    public static TBuilder ServesPublicData<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new PublicDataAttribute());
    }
}
