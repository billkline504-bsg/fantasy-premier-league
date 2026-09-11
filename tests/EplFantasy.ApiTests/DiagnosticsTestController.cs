using EplFantasy.Infrastructure.Idempotency;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EplFantasy.ApiTests;

/// <summary>
/// Test-only endpoints that exist purely to exercise IT-F14's cross-cutting middleware
/// (ProblemDetails error shaping, the "auth" rate-limiting policy, Idempotency-Key handling)
/// end-to-end against a real host. None of these routes ship in the actual API — no feature task
/// has built a real login/draft-pick/roster-submission controller yet for that middleware to
/// attach to, so <see cref="EplFantasyApiFactory"/> adds this controller's assembly as an
/// application part purely for these tests.
/// </summary>
[ApiController]
[Route("api/v1/diagnostics")]
public class DiagnosticsTestController : ControllerBase
{
    [HttpGet("throw-domain-exception")]
    public IActionResult ThrowDomainException() => throw new TestDomainException();

    [HttpGet("throw-unhandled")]
    public IActionResult ThrowUnhandled() => throw new InvalidOperationException("boom - must never reach the client");

    // "auth" must match RateLimitOptions.PolicyName's default (RateLimitingServiceCollectionExtensions).
    [HttpGet("rate-limited")]
    [EnableRateLimiting("auth")]
    public IActionResult RateLimited() => Ok(new { ok = true });

    [HttpPost("idempotent")]
    [Idempotent]
    public IActionResult Idempotent() => new ObjectResult(new { id = Guid.NewGuid(), createdAt = DateTimeOffset.UtcNow }) { StatusCode = StatusCodes.Status201Created };
}

public sealed class TestDomainException() : DomainException("a test aggregate refused this operation")
{
    public override string ErrorCode => "test_domain_failure";
}
