using Convenciones.ServiceDefaults;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Converts any unhandled exception into an RFC 9457 problem response, with the fields the API
/// contract promises (<c>code</c>, <c>status</c>, <c>correlationId</c>).
/// </summary>
/// <remarks>
/// The exception message is never returned to the caller, and the exception is logged once, with
/// the correlation id that the caller is given. That is the opposite of the legacy behaviour, where
/// SQL errors were echoed into the page and failures were otherwise silent.
/// </remarks>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var correlationId = httpContext.TraceIdentifier;

        logger.LogError(
            exception,
            "Unhandled exception for {Method} {Path}. CorrelationId={CorrelationId}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            correlationId);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Type = "https://convenciones.app/errors/internal-error",
                Title = "An unexpected error occurred.",
                Status = StatusCodes.Status500InternalServerError,
                Instance = httpContext.Request.Path,
                Extensions =
                {
                    ["code"] = "internal_error",
                    ["correlationId"] = correlationId,
                },
            },
        }).ConfigureAwait(false);
    }
}
