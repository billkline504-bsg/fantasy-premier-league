using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.PlayerData;
using EplFantasy.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// Proves IT-37's createScoreOverride/undoScoreOverride end-to-end: a 201 with the created,
/// active override for the League Administrator named in the request body; 403 for a caller who
/// isn't that League's Administrator, and 401 with no bearer token at all; undoScoreOverride's own
/// route-based ScoreOverrideLeagueAdministrator policy accepts the same League's Administrator, 403s
/// a different League's Administrator, and a second undo attempt surfaces
/// ScoreOverrideAlreadyUndoneException as 409.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class AdminScoreOverrideControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    /// <summary>Seeds a League (through the real API, so the owner becomes its founding Administrator) plus a Player and a PlayerPerformance row (both directly via the DbContext — no creation API exists for either).</summary>
    private async Task<(string OwnerToken, Guid LeagueId, Guid PlayerPerformanceId)> SeedLeagueAndPerformanceAsync(HttpClient client)
    {
        var ownerToken = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = $"Override League {Guid.NewGuid():N}" }));
        var league = await leagueResponse.Content.ReadFromJsonAsync<LeagueDto>();

        Guid performanceId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            var eplSeasonIdentifier = $"season-{Guid.NewGuid():N}"[..16];
            db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
            var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = DateTimeOffset.UtcNow.AddDays(7) };
            db.Gameweeks.Add(gameweek);
            var playerId = Guid.NewGuid();
            db.Players.Add(new Player { PlayerId = playerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Override Player", Position = PlayerPosition.Fwd });
            var performance = new PlayerPerformance
            {
                PlayerPerformanceId = Guid.NewGuid(),
                GameweekId = gameweek.GameweekId,
                PlayerId = playerId,
                MinutesPlayed = 90,
                Goals = 1,
                Source = PerformanceSource.OfficialFpl,
                IsOfficial = true,
                RetrievedAt = DateTimeOffset.UtcNow,
            };
            db.PlayerPerformances.Add(performance);
            await db.SaveChangesAsync();
            performanceId = performance.PlayerPerformanceId;
        }

        return (ownerToken, league!.LeagueId, performanceId);
    }

    [Fact]
    public async Task CreateScoreOverride_by_the_League_Administrator_returns_201()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, playerPerformanceId) = await SeedLeagueAndPerformanceAsync(client);

        var response = await client.SendAsync(Request(
            HttpMethod.Post, "/api/v1/admin/score-overrides", ownerToken,
            new { playerPerformanceId, leagueId, overrideValue = new { goals = 2 }, reason = "video review" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var scoreOverride = await response.Content.ReadFromJsonAsync<ScoreOverrideDto>();
        Assert.Equal(playerPerformanceId, scoreOverride!.PlayerPerformanceId);
        Assert.True(scoreOverride.IsActive);
        Assert.Equal(2, scoreOverride.OverrideValue["goals"]);
        Assert.Equal(1, scoreOverride.OriginalValue["goals"]);
    }

    [Fact]
    public async Task CreateScoreOverride_is_rejected_for_a_caller_who_is_not_that_Leagues_Administrator()
    {
        var client = factory.CreateClient();
        var (_, leagueId, playerPerformanceId) = await SeedLeagueAndPerformanceAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(
            HttpMethod.Post, "/api/v1/admin/score-overrides", outsiderToken,
            new { playerPerformanceId, leagueId, overrideValue = new { goals = 2 } }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateScoreOverride_without_a_bearer_token_returns_401()
    {
        var client = factory.CreateClient();
        var (_, leagueId, playerPerformanceId) = await SeedLeagueAndPerformanceAsync(client);

        var response = await client.SendAsync(Request(
            HttpMethod.Post, "/api/v1/admin/score-overrides", body: new { playerPerformanceId, leagueId, overrideValue = new { goals = 2 } }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UndoScoreOverride_by_the_same_Leagues_Administrator_returns_200()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, playerPerformanceId) = await SeedLeagueAndPerformanceAsync(client);
        var createResponse = await client.SendAsync(Request(
            HttpMethod.Post, "/api/v1/admin/score-overrides", ownerToken,
            new { playerPerformanceId, leagueId, overrideValue = new { goals = 2 } }));
        var created = await createResponse.Content.ReadFromJsonAsync<ScoreOverrideDto>();

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/admin/score-overrides/{created!.ScoreOverrideId}/undo", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var undone = await response.Content.ReadFromJsonAsync<ScoreOverrideDto>();
        Assert.False(undone!.IsActive);
        Assert.NotNull(undone.UndoneAt);
    }

    [Fact]
    public async Task UndoScoreOverride_is_rejected_for_a_different_Leagues_Administrator()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, playerPerformanceId) = await SeedLeagueAndPerformanceAsync(client);
        var createResponse = await client.SendAsync(Request(
            HttpMethod.Post, "/api/v1/admin/score-overrides", ownerToken,
            new { playerPerformanceId, leagueId, overrideValue = new { goals = 2 } }));
        var created = await createResponse.Content.ReadFromJsonAsync<ScoreOverrideDto>();

        var otherOwnerToken = await RegisterAsync(client);
        await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", otherOwnerToken, new { name = $"Other League {Guid.NewGuid():N}" }));

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/admin/score-overrides/{created!.ScoreOverrideId}/undo", otherOwnerToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UndoScoreOverride_rejects_an_already_undone_override_with_409()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, playerPerformanceId) = await SeedLeagueAndPerformanceAsync(client);
        var createResponse = await client.SendAsync(Request(
            HttpMethod.Post, "/api/v1/admin/score-overrides", ownerToken,
            new { playerPerformanceId, leagueId, overrideValue = new { goals = 2 } }));
        var created = await createResponse.Content.ReadFromJsonAsync<ScoreOverrideDto>();
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/admin/score-overrides/{created!.ScoreOverrideId}/undo", ownerToken));

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/admin/score-overrides/{created.ScoreOverrideId}/undo", ownerToken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("score_override_already_undone", body.GetProperty("errorCode").GetString());
    }
}
