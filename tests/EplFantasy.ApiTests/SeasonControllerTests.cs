using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-06 (F-003.4): proves createSeason (League-Administrator-only)/listSeasons/getSeason (any
/// active member) end-to-end against the real host.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class SeasonControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    private async Task<(string OwnerToken, LeagueDto League)> CreateLeagueAsync(HttpClient client)
    {
        var ownerToken = await RegisterAsync(client);
        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "Season Test League" }));
        var league = await response.Content.ReadFromJsonAsync<LeagueDto>();
        return (ownerToken, league!);
    }

    private async Task SeedEplSeasonAsync(string eplSeasonIdentifier)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        if (!await db.EplSeasons.AnyAsync(s => s.EplSeasonIdentifier == eplSeasonIdentifier))
        {
            db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task CreateSeason_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (_, league) = await CreateLeagueAsync(client);
        var otherToken = await RegisterAsync(client);
        await SeedEplSeasonAsync("2026/27");

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons", otherToken, new { eplSeasonIdentifier = "2026/27", startDate = "2026-08-15" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateSeason_then_getSeason_and_listSeasons_round_trip()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        await SeedEplSeasonAsync("2026/27");

        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons", ownerToken, new { eplSeasonIdentifier = "2026/27", startDate = "2026-08-15" }));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var season = await createResponse.Content.ReadFromJsonAsync<SeasonDto>();
        Assert.Equal(league.LeagueId, season!.LeagueId);
        Assert.Equal("2026/27", season.EplSeasonIdentifier);
        Assert.Equal("Setup", season.Status);
        Assert.Equal(new DateOnly(2026, 8, 15), season.StartDate);
        Assert.Null(season.EndDate);

        var getResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/seasons/{season.SeasonId}", ownerToken));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<SeasonDto>();
        Assert.Equal(season.SeasonId, fetched!.SeasonId);

        var listResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/seasons", ownerToken));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<List<SeasonDto>>();
        Assert.Single(list!, s => s.SeasonId == season.SeasonId);
    }

    [Fact]
    public async Task ListSeasons_filters_by_status()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        await SeedEplSeasonAsync("2026/27");
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons", ownerToken, new { eplSeasonIdentifier = "2026/27", startDate = "2026-08-15" }));

        var matching = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/seasons?status=Setup", ownerToken));
        var nonMatching = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/seasons?status=Completed", ownerToken));

        Assert.Equal(HttpStatusCode.OK, matching.StatusCode);
        Assert.Single(await matching.Content.ReadFromJsonAsync<List<SeasonDto>>() ?? []);
        Assert.Empty(await nonMatching.Content.ReadFromJsonAsync<List<SeasonDto>>() ?? []);
    }

    [Fact]
    public async Task GetSeason_is_rejected_for_a_caller_who_is_not_an_active_member()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);
        await SeedEplSeasonAsync("2026/27");
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons", ownerToken, new { eplSeasonIdentifier = "2026/27", startDate = "2026-08-15" }));
        var season = await createResponse.Content.ReadFromJsonAsync<SeasonDto>();
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{league.LeagueId}/seasons/{season!.SeasonId}", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateSeason_with_an_unknown_epl_season_identifier_returns_400()
    {
        var client = factory.CreateClient();
        var (ownerToken, league) = await CreateLeagueAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons", ownerToken, new { eplSeasonIdentifier = "2099/00", startDate = "2099-08-15" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("epl_season_not_found", body.GetProperty("errorCode").GetString());
    }
}
