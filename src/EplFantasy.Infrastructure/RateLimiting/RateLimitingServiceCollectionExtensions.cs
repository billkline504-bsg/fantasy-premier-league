using System.Threading.RateLimiting;
using EplFantasy.Administration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EplFantasy.Infrastructure.RateLimiting;

public static class RateLimitingServiceCollectionExtensions
{
    /// <summary>
    /// Architecture §11/BR-169: registers a named rate-limiting policy (<see cref="RateLimitOptions.PolicyName"/>,
    /// "auth" by default) for `/auth/*` and other abuse-prone endpoints, keyed per client IP. No
    /// controller opts into it yet — IT-01's login/registration controller is still a future feature
    /// task — but the policy and its BR-170/BR-327 audit trail (every block writes a
    /// <c>security_events</c> row via <see cref="ISecurityEventRecorder"/>) are ready the moment one
    /// does, via <c>[EnableRateLimiting(RateLimitOptions.PolicyName-value)]</c> on that controller's actions.
    /// </summary>
    public static IServiceCollection AddEplFantasyRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.AddSingleton(options);

        services.AddRateLimiter(limiterOptions =>
        {
            limiterOptions.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                var recorder = context.HttpContext.RequestServices.GetRequiredService<ISecurityEventRecorder>();
                var remoteIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                await recorder.RecordAsync(
                    SecurityEventType.RateLimitBlocked,
                    endpoint: context.HttpContext.Request.Path,
                    scope: $"IP {remoteIp}",
                    detail: $"Rate limit exceeded for policy \"{options.PolicyName}\" ({options.PermitLimit} requests per {options.WindowSeconds}s).",
                    cancellationToken);
            };

            limiterOptions.AddPolicy(options.PolicyName, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.PermitLimit,
                        Window = TimeSpan.FromSeconds(options.WindowSeconds),
                        QueueLimit = options.QueueLimit,
                    }));
        });

        return services;
    }
}
