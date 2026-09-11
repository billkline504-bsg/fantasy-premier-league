using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EplFantasy.Infrastructure.ErrorHandling;

/// <summary>
/// .NET 8's <see cref="IExceptionHandler"/> catch-all (Architecture §9.3): every unhandled
/// exception becomes a <c>ProblemDetails</c> response instead of leaking a stack trace to the
/// client. A <see cref="DomainException"/> (an aggregate rejecting an invalid state transition) is
/// an *expected* rejection reported as 400 Bad Request using its own <see cref="DomainException.ErrorCode"/>
/// and message; anything else is genuinely unexpected, logged in full server-side (BR-240) via
/// <see cref="ILogger"/>, and reported to the client as a generic 500 with no exception detail.
/// The <c>correlationId</c> extension field is added uniformly for every ProblemDetails response —
/// this one included — by <see cref="ProblemDetailsServiceCollectionExtensions"/>'s
/// <c>CustomizeProblemDetails</c> hook, not here, so it also covers responses this handler never
/// sees (framework-generated 400s from `[ApiController]` model validation, 404s, etc.).
/// </summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problemDetailsService, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, errorCode, detail) = exception switch
        {
            DomainException domainException => (domainException.StatusCode, domainException.ErrorCode, domainException.Message),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred."),
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning(exception, "{ErrorCode} rejected {Method} {Path}", errorCode, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Detail = detail,
            Instance = httpContext.Request.Path,
        };
        problemDetails.Extensions["errorCode"] = errorCode;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }
}
