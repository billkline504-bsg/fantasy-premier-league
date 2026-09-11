using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-14 (F-001.3): proves getCurrentUser and updateUsername end-to-end against the real host —
/// BR-004/BR-266's uniqueness re-validation and BR-270's identity-preserving rename. IT-15
/// (F-001.4) adds retireCurrentUser — a retired account can no longer log in, and its username
/// frees up for a different user to register with (BR-298).
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class UserControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    private const string StrongPassword = "correct-horse-battery-staple-97!";

    private async Task<(string AccessToken, string Username)> RegisterAsync(HttpClient client)
    {
        var username = $"u{Guid.NewGuid():N}"[..16];
        var email = $"{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        return (tokens!.AccessToken, username);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? accessToken = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    [Fact]
    public async Task GetCurrentUser_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Get, "/api/v1/users/me"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCurrentUser_returns_the_callers_own_profile()
    {
        var client = factory.CreateClient();
        var (accessToken, username) = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var self = await response.Content.ReadFromJsonAsync<UserSelfDto>();
        Assert.Equal(username, self!.Username);
    }

    [Fact]
    public async Task UpdateUsername_changes_the_username_and_the_new_one_can_log_in()
    {
        var client = factory.CreateClient();
        var (accessToken, username) = await RegisterAsync(client);
        var newUsername = $"{username}renamed"[..Math.Min(24, $"{username}renamed".Length)];

        var response = await client.SendAsync(Request(HttpMethod.Put, "/api/v1/users/me", accessToken, new { username = newUsername }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var self = await response.Content.ReadFromJsonAsync<UserSelfDto>();
        Assert.Equal(newUsername, self!.Username);

        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new { usernameOrEmail = newUsername, password = StrongPassword });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }

    [Fact]
    public async Task UpdateUsername_returns_409_when_the_name_is_already_taken()
    {
        var client = factory.CreateClient();
        var (_, takenUsername) = await RegisterAsync(client);
        var (accessToken, _) = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/api/v1/users/me", accessToken, new { username = takenUsername }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("username_taken", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task UpdateUsername_with_a_malformed_username_is_rejected_by_DTO_level_validation()
    {
        var client = factory.CreateClient();
        var (accessToken, _) = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/api/v1/users/me", accessToken, new { username = "not a valid username" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task UpdateUsername_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Put, "/api/v1/users/me", body: new { username = "irrelevant" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RetireCurrentUser_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Post, "/api/v1/users/me/retire"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RetireCurrentUser_then_login_is_rejected()
    {
        var client = factory.CreateClient();
        var (accessToken, username) = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/users/me/retire", accessToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new { usernameOrEmail = username, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);
    }

    [Fact]
    public async Task RetireCurrentUser_frees_the_username_for_a_different_user_to_register_with()
    {
        var client = factory.CreateClient();
        var (accessToken, username) = await RegisterAsync(client);
        await client.SendAsync(Request(HttpMethod.Post, "/api/v1/users/me/retire", accessToken));

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email = $"{Guid.NewGuid():N}@example.com", password = StrongPassword });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
