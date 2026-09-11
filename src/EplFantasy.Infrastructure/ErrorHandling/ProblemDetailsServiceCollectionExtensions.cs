using EplFantasy.Infrastructure.Correlation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EplFantasy.Infrastructure.ErrorHandling;

public static class ProblemDetailsServiceCollectionExtensions
{
    /// <summary>
    /// Wires the ProblemDetails error model Architecture §9.3 asks for: <see cref="GlobalExceptionHandler"/>
    /// as the catch-all for unhandled exceptions, plus a <c>CustomizeProblemDetails</c> hook that
    /// stamps <c>correlationId</c> (BR-241) onto *every* ProblemDetails response — not just the ones
    /// GlobalExceptionHandler produces, but also framework-generated ones (`[ApiController]` model
    /// validation, 404s via UseStatusCodePages) that never reach it — and fills in a default
    /// <c>errorCode</c> for those framework-generated responses, since only GlobalExceptionHandler's
    /// own responses set one directly.
    /// </summary>
    public static IServiceCollection AddEplFantasyProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var correlationId = context.HttpContext.Items.TryGetValue(CorrelationIdMiddleware.HttpContextItemKey, out var id)
                    ? id?.ToString()
                    : null;
                context.ProblemDetails.Extensions["correlationId"] = correlationId;

                if (!context.ProblemDetails.Extensions.ContainsKey("errorCode"))
                {
                    context.ProblemDetails.Extensions["errorCode"] = context.ProblemDetails.Status switch
                    {
                        StatusCodes.Status400BadRequest => "validation_failed",
                        StatusCodes.Status401Unauthorized => "unauthorized",
                        StatusCodes.Status403Forbidden => "forbidden",
                        StatusCodes.Status404NotFound => "not_found",
                        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
                        StatusCodes.Status409Conflict => "conflict",
                        StatusCodes.Status429TooManyRequests => "rate_limit_exceeded",
                        _ => "error",
                    };
                }
            };
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();

        return services;
    }
}
