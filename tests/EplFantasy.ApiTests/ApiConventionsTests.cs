using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Correlation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// Proves IT-F14's global API conventions end-to-end against the real host (Program.cs,
/// unmodified) rather than trusting each middleware's own unit tests to compose correctly —
/// ProblemDetails error shaping (with errorCode/correlationId), the "auth" rate-limiting policy
/// writing a security_events row on rejection, correlation-ID propagation, Idempotency-Key replay,
/// and versioned routing under /api/v1.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class ApiConventionsTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    [Fact]
    public async Task Health_check_is_reachable_under_the_versioned_route()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_DomainException_becomes_a_400_ProblemDetails_with_its_own_errorCode()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/diagnostics/throw-domain-exception");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("test_domain_failure", body.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task An_unhandled_exception_becomes_a_500_ProblemDetails_that_never_leaks_the_exception_message()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/diagnostics/throw-unhandled");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("internal_error", body.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("boom", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_unmatched_route_returns_a_404_ProblemDetails()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/this-route-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task A_caller_supplied_correlation_id_is_echoed_back()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "test-correlation-42");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal("test-correlation-42", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public async Task A_request_with_no_correlation_id_gets_one_generated()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/health");

        var generated = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.True(Guid.TryParse(generated, out _));
    }

    [Fact]
    public async Task Repeating_an_Idempotency_Key_replays_the_first_response_instead_of_creating_twice()
    {
        var client = factory.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/diagnostics/idempotent");
        request1.Headers.Add("Idempotency-Key", "test-idem-key-1");
        var response1 = await client.SendAsync(request1);
        var body1 = await response1.Content.ReadFromJsonAsync<JsonElement>();

        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/diagnostics/idempotent");
        request2.Headers.Add("Idempotency-Key", "test-idem-key-1");
        var response2 = await client.SendAsync(request2);
        var body2 = await response2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, response2.StatusCode);
        Assert.Equal(body1.GetProperty("id").GetString(), body2.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Two_different_Idempotency_Keys_each_execute_independently()
    {
        var client = factory.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/diagnostics/idempotent");
        request1.Headers.Add("Idempotency-Key", "test-idem-key-a");
        var response1 = await client.SendAsync(request1);
        var body1 = await response1.Content.ReadFromJsonAsync<JsonElement>();

        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/diagnostics/idempotent");
        request2.Headers.Add("Idempotency-Key", "test-idem-key-b");
        var response2 = await client.SendAsync(request2);
        var body2 = await response2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.NotEqual(body1.GetProperty("id").GetString(), body2.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Exceeding_the_auth_rate_limit_returns_429_and_writes_a_security_event()
    {
        var client = factory.CreateClient();
        HttpResponseMessage? rejected = null;

        // RateLimiting:PermitLimit is 50 (EplFantasyApiFactory) — this test has its own dedicated
        // IClassFixture instance (ApiConventionsTests), so it's free to exhaust it entirely.
        for (var i = 0; i < 60 && rejected is null; i++)
        {
            var response = await client.GetAsync("/api/v1/diagnostics/rate-limited");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response;
            }
        }

        Assert.NotNull(rejected);
        var body = await rejected!.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rate_limit_exceeded", body.GetProperty("errorCode").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        Assert.True(await db.SecurityEvents.AnyAsync(e => e.Endpoint.Contains("rate-limited")));
    }
}
