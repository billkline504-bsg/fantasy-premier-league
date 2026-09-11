using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Administration;
using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-02 (F-011.3): proves the three admin/security endpoints are really System-Administrator-only
/// (ADR-007 — never satisfied by a League Administrator) and reflect real state — the configured
/// "auth" rate-limit policy, persisted <c>security_events</c> rows in chronological pages, and the
/// platform's actual (bearer-JWT-only, no cookie/antiforgery) authentication posture.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class SecurityControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    private const string StrongPassword = "correct-horse-battery-staple-97!";

    private async Task<string> RegisterAsync(HttpClient client)
    {
        var username = $"u{Guid.NewGuid():N}"[..16];
        var email = $"{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        return tokens!.AccessToken;
    }

    private async Task<string> RegisterSystemAdministratorAsync(HttpClient client)
    {
        var username = $"u{Guid.NewGuid():N}"[..16];
        var email = $"{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();

        // IsSystemAdministrator is provisioned out-of-band (ADR-007) — there is no self-service
        // endpoint for it, so the test flips the flag directly, then reuses the already-issued
        // access token; SystemAdministratorAuthorizationHandler checks the User row live, not a
        // cached claim, so the pre-existing token picks up the change immediately.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var user = await db.Users.SingleAsync(u => u.UserId == tokens!.User.UserId);
        user.IsSystemAdministrator = true;
        await db.SaveChangesAsync();

        return tokens!.AccessToken;
    }

    private static HttpRequestMessage Get(string path, string? accessToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    [Theory]
    [InlineData("/api/v1/admin/security/rate-limits")]
    [InlineData("/api/v1/admin/security/events")]
    [InlineData("/api/v1/admin/security/csrf-status")]
    public async Task Without_a_bearer_token_every_endpoint_returns_401(string path)
    {
        var response = await factory.CreateClient().SendAsync(Get(path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/admin/security/rate-limits")]
    [InlineData("/api/v1/admin/security/events")]
    [InlineData("/api/v1/admin/security/csrf-status")]
    public async Task A_non_system_administrator_caller_is_rejected_with_403(string path)
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Get(path, accessToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetRateLimitConfiguration_reflects_the_live_auth_policy()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterSystemAdministratorAsync(client);

        var response = await client.SendAsync(Get("/api/v1/admin/security/rate-limits", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rules = await response.Content.ReadFromJsonAsync<List<RateLimitRuleDto>>();
        var rule = Assert.Single(rules!);
        Assert.Equal("/api/v1/auth/*", rule.EndpointPattern);
        // EplFantasyApiFactory overrides RateLimiting__PermitLimit=50, RateLimiting__WindowSeconds=60.
        Assert.Equal(50, rule.MaxRequests);
        Assert.Equal(60, rule.WindowSeconds);
    }

    [Fact]
    public async Task GetCsrfStatus_reports_the_platforms_actual_bearer_only_posture()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterSystemAdministratorAsync(client);

        var response = await client.SendAsync(Get("/api/v1/admin/security/csrf-status", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<CsrfStatusDto>();
        // ADR-006/Program.cs: bearer-JWT authentication only — no cookie scheme, no AddAntiforgery().
        Assert.False(status!.CookieAuthenticationEnabled);
        Assert.False(status.CsrfMiddlewareActive);
    }

    [Fact]
    public async Task GetSecurityEvents_surfaces_a_previously_recorded_event()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterSystemAdministratorAsync(client);
        var marker = $"/marker-{Guid.NewGuid():N}";

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.SecurityEvents.Add(new SecurityEvent
            {
                SecurityEventId = Guid.NewGuid(),
                EventType = SecurityEventType.RateLimitBlocked,
                Endpoint = marker,
                Scope = "IP 203.0.113.50",
                Detail = "test-recorded event",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Get("/api/v1/admin/security/events?limit=200", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<SecurityEventPageDto>();
        var found = Assert.Single(page!.Items.Where(e => e.Endpoint == marker));
        Assert.Equal("RateLimitBlocked", found.EventType);
        Assert.Equal("IP 203.0.113.50", found.Scope);
    }

    [Fact]
    public async Task GetSecurityEvents_pages_newest_first_via_the_returned_cursor()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterSystemAdministratorAsync(client);
        var runId = Guid.NewGuid().ToString("N");
        var t0 = DateTimeOffset.UtcNow;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.SecurityEvents.AddRange(
                NewEvent($"/run-{runId}/oldest", t0),
                NewEvent($"/run-{runId}/middle", t0.AddSeconds(1)),
                NewEvent($"/run-{runId}/newest", t0.AddSeconds(2)));
            await db.SaveChangesAsync();
        }

        var firstPageResponse = await client.SendAsync(Get("/api/v1/admin/security/events?limit=1", accessToken));
        Assert.Equal(HttpStatusCode.OK, firstPageResponse.StatusCode);
        var firstPage = await firstPageResponse.Content.ReadFromJsonAsync<SecurityEventPageDto>();
        var firstItem = Assert.Single(firstPage!.Items);
        Assert.Equal($"/run-{runId}/newest", firstItem.Endpoint);
        Assert.NotNull(firstPage.NextCursor);

        var secondPageResponse = await client.SendAsync(Get($"/api/v1/admin/security/events?limit=1&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}", accessToken));
        Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
        var secondPage = await secondPageResponse.Content.ReadFromJsonAsync<SecurityEventPageDto>();
        var secondItem = Assert.Single(secondPage!.Items);
        Assert.Equal($"/run-{runId}/middle", secondItem.Endpoint);

        return;

        static SecurityEvent NewEvent(string endpoint, DateTimeOffset occurredAt) => new()
        {
            SecurityEventId = Guid.NewGuid(),
            EventType = SecurityEventType.RateLimitBlocked,
            Endpoint = endpoint,
            Scope = "IP 203.0.113.60",
            Detail = "pagination test event",
            OccurredAt = occurredAt,
        };
    }

    [Fact]
    public async Task GetSecurityEvents_with_a_malformed_cursor_returns_400()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterSystemAdministratorAsync(client);

        var response = await client.SendAsync(Get("/api/v1/admin/security/events?cursor=not-valid-base64!!", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", body.GetProperty("errorCode").GetString());
    }
}
