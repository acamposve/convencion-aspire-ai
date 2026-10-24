using Convenciones.Identity;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// Who the caller is, which company they belong to, and what they may do.
/// </summary>
/// <remarks>
/// The client needs this to render actions honestly: a button the caller cannot use should not be
/// displayed. It stays advisory — the API enforces its own capabilities regardless, because a
/// client that hides a control is a convenience, not a control.
/// </remarks>
public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/me", (ISessionAccessor accessor) =>
            {
                var session = accessor.Session;
                if (session is null)
                {
                    // Unreachable when the middleware ran; kept because a null here means the
                    // pipeline is wrong and returning 200 would be a lie.
                    return Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Forbidden",
                        detail: "No session was resolved for this request.");
                }

                return Results.Ok(new MeResponse(
                    session.UserId,
                    session.Email,
                    session.DisplayName,
                    session.CompanyId,
                    session.Capabilities.OrderBy(capability => capability, StringComparer.Ordinal).ToArray()));
            })
            .RequiresActiveSession()
            .WithName("GetMe");

        return app;
    }
}

public sealed record MeResponse(
    Guid UserId,
    string Email,
    string? DisplayName,
    Guid? CompanyId,
    IReadOnlyList<string> Capabilities);
