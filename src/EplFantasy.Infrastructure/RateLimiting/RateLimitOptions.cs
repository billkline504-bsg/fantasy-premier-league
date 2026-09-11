namespace EplFantasy.Infrastructure.RateLimiting;

/// <summary>Architecture §11/BR-169: the "auth" policy applied to `/auth/*` and other abuse-prone endpoints. Bound from the "RateLimiting" configuration section; every property has a sensible default so an app with no such section still gets real protection.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>The name feature-task controllers reference via <c>[EnableRateLimiting(...)]</c>.</summary>
    public string PolicyName { get; init; } = "auth";

    public int PermitLimit { get; init; } = 10;

    public int WindowSeconds { get; init; } = 60;

    public int QueueLimit { get; init; } = 0;
}
