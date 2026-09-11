using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// Proves the "auth" rate-limiting policy (IT-F14) is really applied to a real production
/// endpoint (AuthController.Register), not just the IT-F14 diagnostics controller
/// (ApiConventionsTests already proves the middleware itself works generically). Deliberately its
/// own test class — and so its own <see cref="EplFantasyApiFactory"/> instance, per
/// <see cref="ApiTestCollection"/>'s remarks — because exhausting the rate limit is the whole
/// point, and doing that against the same fixture <see cref="AuthControllerTests"/> uses would
/// fail every one of that class's functional tests that happened to run afterward.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class AuthRateLimitingTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    [Fact]
    public async Task Repeatedly_registering_eventually_trips_the_auth_rate_limit()
    {
        var username = $"u{Guid.NewGuid():N}"[..16];
        var email = $"{Guid.NewGuid():N}@example.com";
        var client = factory.CreateClient();
        HttpResponseMessage? rejected = null;

        // Same payload every time (username_taken after the first) — the rate limiter counts
        // requests regardless of business outcome. RateLimiting:PermitLimit is 50
        // (EplFantasyApiFactory); this class's fixture is exclusively its own.
        for (var i = 0; i < 60 && rejected is null; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = "correct-horse-battery-staple-97!" });
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response;
            }
        }

        Assert.NotNull(rejected);
    }
}
