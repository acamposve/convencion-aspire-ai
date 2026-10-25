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
/// Declares the capability an endpoint needs.
/// </summary>
/// <remarks>
/// The capability is enforced twice, and deliberately so: here, against the session read from the
/// database, and again by the RLS policy on the table being touched. The API check produces a clear
/// 403; the policy is what actually protects the data if the API check were ever removed by mistake.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresCapabilityAttribute(string capability) : Attribute
{
    public string Capability { get; } = !string.IsNullOrWhiteSpace(capability)
        ? capability
        : throw new ArgumentException("A capability key is required.", nameof(capability));
}

public static class EndpointSecurityExtensions
{
    public static TBuilder RequiresCapability<TBuilder>(this TBuilder builder, string capability)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RequiresCapabilityAttribute(capability));
    }

    public static TBuilder RequiresActiveSession<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RequiresActiveSessionAttribute());
    }
}
