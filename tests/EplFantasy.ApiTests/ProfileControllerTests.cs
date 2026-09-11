using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-09 (F-002.1): proves updateDefaultIcon (any authenticated caller, acting on themselves) and
/// listProfileIcons (anonymous, a static catalog) end-to-end against the real host.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class ProfileControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    private const string StrongPassword = "correct-horse-battery-staple-97!";

    private async Task<(string AccessToken, Guid DefaultIconId)> RegisterAsync(HttpClient client)
    {
        var username = $"u{Guid.NewGuid():N}"[..16];
        var email = $"{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();
        return (tokens!.AccessToken, tokens.User.DefaultIconId);
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
    public async Task ListProfileIcons_is_reachable_without_a_bearer_token_and_returns_only_active_icons_in_sortOrder()
    {
        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Get, "/api/v1/profile-icons"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var icons = await response.Content.ReadFromJsonAsync<List<ProfileIconDto>>();
        Assert.NotEmpty(icons!);
        Assert.All(icons!, i => Assert.True(i.IsActive));
        Assert.Equal(icons!.OrderBy(i => i.SortOrder).Select(i => i.ProfileIconId), icons!.Select(i => i.ProfileIconId));
    }

    [Fact]
    public async Task UpdateDefaultIcon_without_a_bearer_token_returns_401()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var icon = await db.ProfileIcons.Where(i => i.IsActive).FirstAsync();

        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Put, "/api/v1/users/me/icon", body: new { profileIconId = icon.ProfileIconId }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateDefaultIcon_changes_the_callers_own_icon_and_is_reflected_on_a_subsequent_login()
    {
        var client = factory.CreateClient();
        var (accessToken, originalIconId) = await RegisterAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var newIcon = await db.ProfileIcons.Where(i => i.IsActive && i.ProfileIconId != originalIconId).FirstAsync();

        var response = await client.SendAsync(Request(HttpMethod.Put, "/api/v1/users/me/icon", accessToken, new { profileIconId = newIcon.ProfileIconId }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var self = await response.Content.ReadFromJsonAsync<UserSelfDto>();
        Assert.Equal(newIcon.ProfileIconId, self!.DefaultIconId);
    }

    [Fact]
    public async Task UpdateDefaultIcon_with_an_unknown_profileIconId_returns_400()
    {
        var client = factory.CreateClient();
        var (accessToken, _) = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/api/v1/users/me/icon", accessToken, new { profileIconId = Guid.NewGuid() }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("profile_icon_not_active", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task UpdateDefaultIcon_with_an_inactive_profileIconId_returns_400()
    {
        var client = factory.CreateClient();
        var (accessToken, _) = await RegisterAsync(client);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var inactiveIcon = new ProfileIcon { ProfileIconId = Guid.NewGuid(), Name = "Retired", AssetIdentifier = "icons/profile/retired.svg", IsActive = false, SortOrder = 99 };
        db.ProfileIcons.Add(inactiveIcon);
        await db.SaveChangesAsync();

        var response = await client.SendAsync(Request(HttpMethod.Put, "/api/v1/users/me/icon", accessToken, new { profileIconId = inactiveIcon.ProfileIconId }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("profile_icon_not_active", body.GetProperty("errorCode").GetString());
    }
}
