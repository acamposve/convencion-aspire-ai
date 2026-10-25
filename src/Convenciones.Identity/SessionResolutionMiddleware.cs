using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Convenciones.Identity;

/// <summary>
/// Resolves the caller's session once per request and enforces the endpoint's declared capability.
/// </summary>
/// <remarks>
/// It runs after authentication and authorization, so an anonymous request has already been refused
/// with 401 by the fallback policy. What is decided here is whether an <em>authenticated</em> caller
/// is allowed: a blocked account, an expired account, or a missing profile is 403 — never a default
/// identity, and never a silent fallback to "some access".
/// </remarks>
public sealed class SessionResolutionMiddleware(RequestDelegate next, ILogger<SessionResolutionMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        ISessionResolver resolver,
        ISessionAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();

        // No endpoint, or a public one: nothing to resolve.
        if (endpoint is null || EndpointSecurityValidator.IsDeclared(endpoint) is false)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null)
        {
            // Anonymous, but not necessarily unprivileged: only endpoints that declare public data
            // switch the database role to anon. The bootstrap endpoint stays on the login's role.
            if (endpoint.Metadata.GetMetadata<PublicDataAttribute>() is not null)
            {
                accessor.UseAnonymousDatabaseRole = true;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        var userId = context.User.GetUserId();
        if (userId is null)
        {
            // Authenticated but without a usable subject: refuse rather than guess.
            await Problem(context, "unauthorized", "The token does not identify a user.").ConfigureAwait(false);
            return;
        }

        var session = await resolver.ResolveAsync(userId.Value, context.RequestAborted).ConfigureAwait(false);

        if (session is null)
        {
            logger.LogWarning("Authenticated subject {UserId} has no account; refusing.", userId);
            await Problem(context, "no_profile",
                "No account exists for this identity. Access is not granted by default.").ConfigureAwait(false);
            return;
        }

        if (!session.IsActive)
        {
            logger.LogWarning(
                "Refusing {UserId}: {Reason}.", session.UserId, session.InactiveReason);
            await Problem(context, "account_inactive",
                "This account is not active.").ConfigureAwait(false);
            return;
        }

        accessor.Session = session;

        var required = endpoint.Metadata.GetMetadata<RequiresCapabilityAttribute>();
        if (required is not null)
        {
            // Every declared capability is required. Checking only the first would silently weaken
            // any endpoint that needs two, and the weakened rule would still pass a single test.
            var missing = required.Capabilities
                .Where(capability => !session.HasCapability(capability))
                .ToArray();

            if (missing.Length > 0)
            {
                logger.LogInformation(
                    "User {UserId} lacks capability {Capabilities}.",
                    session.UserId,
                    string.Join(", ", missing));

                await Problem(context, ErrorCodes.Forbidden,
                    $"The capability '{string.Join("' and '", missing)}' is required.").ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static Task Problem(HttpContext context, string code, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: detail,
                extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(context);
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Reads <c>sub</c> without relying on inbound claim mapping, which would rename it to
    /// <c>nameidentifier</c> and silently break token-subject resolution.
    /// </summary>
    public static Guid? GetUserId(this System.Security.Claims.ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var value = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(value, out var id) ? id : null;
    }
}
