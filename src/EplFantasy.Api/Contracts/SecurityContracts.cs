namespace EplFantasy.Api.Contracts;

// IT-02 (F-011.3): DTOs for the three System-Administrator-only security/abuse-monitoring
// endpoints (OpenAPI RateLimitRule/SecurityEvent/SecurityEventPage/CsrfStatus schemas).

/// <summary>BR-327: mirrors the live <c>RateLimitOptions</c> the "auth" policy actually runs with, never a separate hard-coded description of the same policy.</summary>
public sealed class RateLimitRuleDto
{
    public required string EndpointPattern { get; init; }
    public required int WindowSeconds { get; init; }
    public required int MaxRequests { get; init; }
}

public sealed class SecurityEventDto
{
    public required Guid SecurityEventId { get; init; }
    public required string EventType { get; init; }
    public required string Endpoint { get; init; }
    public required string Scope { get; init; }
    public required string Detail { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

public sealed class SecurityEventPageDto
{
    public required IReadOnlyList<SecurityEventDto> Items { get; init; }
    public string? NextCursor { get; init; }
}

/// <summary>BR-328: derived live from the registered authentication schemes and services rather than a static claim, so it keeps telling the truth if cookie authentication is ever added.</summary>
public sealed class CsrfStatusDto
{
    public required bool CookieAuthenticationEnabled { get; init; }
    public required bool CsrfMiddlewareActive { get; init; }
}
