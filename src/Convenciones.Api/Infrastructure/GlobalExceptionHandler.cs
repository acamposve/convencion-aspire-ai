using Convenciones.Identity;
using Convenciones.ServiceDefaults;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Converts any unhandled exception into an RFC 9457 problem response, with the fields the API
/// contract promises (<c>code</c>, <c>status</c>, <c>correlationId</c>).
/// </summary>
/// <remarks>
/// <para>
/// The exception message is never returned to the caller, and the exception is logged once, with
/// the correlation id that the caller is given. That is the opposite of the legacy behaviour, where
/// SQL errors were echoed into the page and failures were otherwise silent.
/// </para>
/// <para>
/// A request the framework could not even read — a body that is not valid JSON for the endpoint's
/// contract, for example a number where a string is expected — arrives here as
/// <see cref="BadHttpRequestException"/> with status 400. It is reported as
/// <c>malformed_request</c>, because the contract says no endpoint returns a bare status without a
/// problem body (<c>api-contracts.md</c> §2, AC-A1). Without this branch the framework answers 400
/// with an empty body, which is exactly the silent failure the contract exists to remove.
/// </para>
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

        if (exception is BadHttpRequestException badRequest
            && RequestFailureProblem.For(httpContext, badRequest.StatusCode) is { } problem)
        {
            logger.LogWarning(
                "Refused request for {Method} {Path}: {Reason}. CorrelationId={CorrelationId}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                exception.Message,
                correlationId);

            httpContext.Response.StatusCode = badRequest.StatusCode;

            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = problem,
            }).ConfigureAwait(false);
        }

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
                    ["code"] = ErrorCodes.InternalError,
                    ["correlationId"] = correlationId,
                },
            },
        }).ConfigureAwait(false);
    }
}
