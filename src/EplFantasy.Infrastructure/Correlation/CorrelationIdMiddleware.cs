using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace EplFantasy.Infrastructure.Correlation;

/// <summary>
/// BR-241/Architecture §9.3/§12.3: tags every request with a correlation ID — reusing the caller's
/// own <see cref="HeaderName"/> value if it sent one, generating a fresh one otherwise — echoes it
/// back on the response, and threads it through every log line the rest of the pipeline writes for
/// this request via <see cref="ILogger.BeginScope{TState}"/>. <see cref="GlobalExceptionHandler"/>
/// reads the same <see cref="HttpContext.Items"/> entry to populate ProblemDetails.correlationId.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string HttpContextItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var provided) && !string.IsNullOrWhiteSpace(provided)
            ? provided.ToString()
            : Guid.NewGuid().ToString();

        context.Items[HttpContextItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}

public static class CorrelationIdApplicationBuilderExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
