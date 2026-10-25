using Convenciones.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// The problem response for a request the framework refused before an endpoint could run.
/// </summary>
/// <remarks>
/// <para>
/// Three statuses are mapped: a body the framework cannot read or a parameter it cannot bind (400), a
/// content type it does not accept (415), and a payload it reports as too large (413). The contract
/// names a code for each and forbids a bare status, so they share this one mapping — used both by
/// <see cref="GlobalExceptionHandler"/> (when the framework throws) and by the status-code-pages
/// middleware (when it writes the status itself). The status is always the one the framework chose;
/// only the body is added. A body above the transport limit does not always arrive here as 413 — the
/// server can reset the connection before the API sees it — which is why that limit is documented as a
/// known gap rather than claimed as covered.
/// </para>
/// <para>
/// Anything else returns <see langword="null"/> so no other response is rewritten.
/// </para>
/// </remarks>
internal static class RequestFailureProblem
{
    public static ProblemDetails? For(HttpContext context, int statusCode)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (type, title, code, detail) = statusCode switch
        {
            StatusCodes.Status400BadRequest => (
                "https://convenciones.app/errors/malformed-request",
                "Malformed request",
                ErrorCodes.MalformedRequest,
                "The request body or parameters could not be read as the endpoint's contract requires."),
            StatusCodes.Status413PayloadTooLarge => (
                "https://convenciones.app/errors/payload-too-large",
                "Payload too large",
                ErrorCodes.PayloadTooLarge,
                "The request body exceeds the configured transport limit."),
            StatusCodes.Status415UnsupportedMediaType => (
                "https://convenciones.app/errors/unsupported-media-type",
                "Unsupported media type",
                ErrorCodes.UnsupportedMediaType,
                "This endpoint accepts a JSON request body."),
            _ => (string.Empty, string.Empty, string.Empty, string.Empty),
        };

        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        return new ProblemDetails
        {
            Type = type,
            Title = title,
            Status = statusCode,
            Detail = detail,
            Instance = context.Request.Path,
            Extensions =
            {
                ["code"] = code,
                ["correlationId"] = context.TraceIdentifier,
                ["errors"] = new Dictionary<string, string[]>
                {
                    ["body"] = [detail],
                },
            },
        };
    }
}
