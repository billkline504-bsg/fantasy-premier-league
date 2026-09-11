using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// Proves IT-01's four auth endpoints end-to-end against the real host, real Postgres, and IT-F14's
/// ProblemDetails middleware — not just that AuthController/IUserAccountService work in isolation
/// (UserAccountServiceTests already proves that). The "auth" rate limit itself is proved separately
/// in <see cref="AuthRateLimitingTests"/>, which needs its own isolated fixture instance to
/// deliberately exhaust it — see that class's remarks.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class AuthControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    private const string StrongPassword = "correct-horse-battery-staple-97!";

    private static (string Username, string Email) UniqueIdentity() =>
        ($"u{Guid.NewGuid():N}"[..16], $"{Guid.NewGuid():N}@example.com");

    [Fact]
    public async Task Register_creates_an_account_and_returns_tokens_plus_the_users_own_profile()
    {
        var (username, email) = UniqueIdentity();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
        Assert.Equal(username, body.User.Username);
        Assert.Equal(email, body.User.Email);
        Assert.Equal("Active", body.User.Status);
        Assert.False(body.User.IsSystemAdministrator);
    }

    [Fact]
    public async Task Register_with_an_already_taken_username_returns_409_with_a_specific_errorCode()
    {
        var (username, email) = UniqueIdentity();
        var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var (_, secondEmail) = UniqueIdentity();
        var second = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email = secondEmail, password = StrongPassword });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("username_taken", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Register_with_a_malformed_username_is_rejected_by_DTO_level_validation()
    {
        var client = factory.CreateClient();

        // Contains a space — violates RegisterRequest's [RegularExpression("^[A-Za-z0-9_]+$")].
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username = "not a valid username", email = "x@example.com", password = StrongPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Register_with_a_weak_password_returns_400_with_password_too_weak()
    {
        var (username, email) = UniqueIdentity();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = "aaaaaaaaaaaa" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("password_too_weak", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Login_with_the_correct_password_succeeds()
    {
        var (username, email) = UniqueIdentity();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { usernameOrEmail = username, password = StrongPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        Assert.Equal(username, body!.User.Username);
    }

    [Fact]
    public async Task Login_with_the_wrong_password_returns_401()
    {
        var (username, email) = UniqueIdentity();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { usernameOrEmail = username, password = "totally-wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_credentials", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Refresh_issues_a_new_token_pair_and_burns_the_old_refresh_token()
    {
        var (username, email) = UniqueIdentity();
        var client = factory.CreateClient();
        var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });
        var original = await registerResponse.Content.ReadFromJsonAsync<AuthTokenResponse>();

        var refreshResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original!.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var refreshed = await refreshResponse.Content.ReadFromJsonAsync<AuthTokenResponse>();
        Assert.Equal(username, refreshed!.User.Username);
        Assert.NotEqual(original.RefreshToken, refreshed.RefreshToken);

        // BR-159 rotation: the original refresh token must no longer work.
        var reuseResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = original.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);
    }

    [Fact]
    public async Task Logout_without_a_bearer_token_is_rejected()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = "irrelevant" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_with_a_bearer_token_revokes_the_refresh_token()
    {
        var (username, email) = UniqueIdentity();
        var client = factory.CreateClient();
        var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });
        var tokens = await registerResponse.Content.ReadFromJsonAsync<AuthTokenResponse>();

        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout")
        {
            Content = JsonContent.Create(new { refreshToken = tokens!.RefreshToken }),
        };
        logoutRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var logoutResponse = await client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var reuseResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);
    }
}
