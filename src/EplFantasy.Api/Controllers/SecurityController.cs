using System.Globalization;
using System.Text;
using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-02 (F-011.3): read-only, platform-level security &amp; abuse monitoring for the System
/// Administrator (never a League role, ADR-007) — OpenAPI Specification v1.0's
/// getRateLimitConfiguration/getSecurityEvents/getCsrfStatus. Nothing here writes anything; the
/// `security_events` rows this surfaces are written directly by the rate-limiting middleware
/// (IT-F14, <c>ISecurityEventRecorder</c>) on every enforced block.
/// </summary>
[ApiController]
[Route("api/v1/admin/security")]
[Authorize(Policy = AuthorizationPolicies.SystemAdministrator)]
public class SecurityController(
    EplFantasyDbContext dbContext,
    RateLimitOptions rateLimitOptions,
    IAuthenticationSchemeProvider schemeProvider) : ControllerBase
{
    [HttpGet("rate-limits")]
    public IActionResult GetRateLimitConfiguration()
    {
        // Only the "auth" policy exists today (IT-F14) — reflects RateLimitOptions directly
        // (AP-006: configuration, not a literal) rather than restating its numbers by hand.
        return Ok(new[]
        {
            new RateLimitRuleDto
            {
                EndpointPattern = "/api/v1/auth/*",
                WindowSeconds = rateLimitOptions.WindowSeconds,
                MaxRequests = rateLimitOptions.PermitLimit,
            },
        });
    }

    [HttpGet("events")]
    public async Task<IActionResult> GetSecurityEvents(
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200)
        {
            return BadRequest();
        }

        var query = dbContext.SecurityEvents.AsNoTracking().OrderByDescending(e => e.OccurredAt).AsQueryable();

        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var before))
            {
                return BadRequest();
            }

            query = query.Where(e => e.OccurredAt < before).OrderByDescending(e => e.OccurredAt);
        }

        // Mirrors the ix_security_events_occurred (occurred_at DESC) index the migration already
        // defines for exactly this access pattern (V010).
        var page = await query.Take(limit + 1).ToListAsync(cancellationToken);
        var items = page.Take(limit).ToList();
        var nextCursor = page.Count > limit ? EncodeCursor(items[^1].OccurredAt) : null;

        return Ok(new SecurityEventPageDto
        {
            Items = items
                .Select(e => new SecurityEventDto
                {
                    SecurityEventId = e.SecurityEventId,
                    EventType = e.EventType.ToString(),
                    Endpoint = e.Endpoint,
                    Scope = e.Scope,
                    Detail = e.Detail,
                    OccurredAt = e.OccurredAt,
                })
                .ToList(),
            NextCursor = nextCursor,
        });
    }

    [HttpGet("csrf-status")]
    public async Task<IActionResult> GetCsrfStatus()
    {
        // ADR-006/Program.cs: authentication is bearer-JWT only today, no cookie scheme and no
        // AddAntiforgery() registration — both checked live so this endpoint keeps telling the
        // truth rather than restating that design decision as a hard-coded pair of booleans.
        var schemes = await schemeProvider.GetAllSchemesAsync();
        var cookieAuthenticationEnabled = schemes.Any(s => s.HandlerType.Name.Contains("Cookie", StringComparison.Ordinal));
        var csrfMiddlewareActive = HttpContext.RequestServices.GetService<IAntiforgery>() is not null;

        return Ok(new CsrfStatusDto
        {
            CookieAuthenticationEnabled = cookieAuthenticationEnabled,
            CsrfMiddlewareActive = csrfMiddlewareActive,
        });
    }

    private static string EncodeCursor(DateTimeOffset occurredAt) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(occurredAt.ToString("O", CultureInfo.InvariantCulture)));

    private static bool TryDecodeCursor(string cursor, out DateTimeOffset occurredAt)
    {
        occurredAt = default;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return DateTimeOffset.TryParse(decoded, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out occurredAt);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
