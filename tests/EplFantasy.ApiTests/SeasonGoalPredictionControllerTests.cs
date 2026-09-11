using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.FantasyTeams;
using EplFantasy.Infrastructure;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-07 (F-010.3): proves getSeasonGoalPrediction (owner or any active League member) and
/// submitSeasonGoalPrediction (owner-only, locks at Season start) end-to-end against the real
/// host. F-002.3's own FantasyTeam-creation flow (IT-11) doesn't exist yet, so each test seeds a
/// FantasyTeam row directly, the same precedent AuthorizationHandlerTests already established.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class SeasonGoalPredictionControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    private async Task<(string OwnerToken, Guid LeagueId, Guid SeasonId, Guid FantasyTeamId)> SeedLeagueSeasonAndFantasyTeamAsync(HttpClient client, DateOnly seasonStartDate)
    {
        var ownerToken = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "Prediction API Test League" }));
        var league = await leagueResponse.Content.ReadFromJsonAsync<LeagueDto>();

        var eplSeasonIdentifier = $"season-{Guid.NewGuid():N}"[..16];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
            await db.SaveChangesAsync();
        }

        var seasonResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league!.LeagueId}/seasons", ownerToken, new { eplSeasonIdentifier, startDate = seasonStartDate.ToString("yyyy-MM-dd") }));
        var season = await seasonResponse.Content.ReadFromJsonAsync<SeasonDto>();

        var fantasyTeamId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.FantasyTeams.Add(new FantasyTeam
            {
                FantasyTeamId = fantasyTeamId,
                LeagueMembershipId = league.CreatedByMembershipId,
                SeasonId = season!.SeasonId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return (ownerToken, league.LeagueId, season.SeasonId, fantasyTeamId);
    }

    [Fact]
    public async Task SubmitSeasonGoalPrediction_then_getSeasonGoalPrediction_round_trip()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));
        var path = $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction";

        var submitResponse = await client.SendAsync(Request(HttpMethod.Put, path, ownerToken, new { predictedEplGoals = 1234 }));

        Assert.Equal(HttpStatusCode.OK, submitResponse.StatusCode);
        var submitted = await submitResponse.Content.ReadFromJsonAsync<SeasonGoalPredictionDto>();
        Assert.Equal(1234, submitted!.PredictedEplGoals);
        Assert.Equal(seasonId, submitted.SeasonId);
        Assert.Equal(fantasyTeamId, submitted.FantasyTeamId);

        var getResponse = await client.SendAsync(Request(HttpMethod.Get, path, ownerToken));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<SeasonGoalPredictionDto>();
        Assert.Equal(submitted.PredictionId, fetched!.PredictionId);
    }

    [Fact]
    public async Task GetSeasonGoalPrediction_returns_404_when_not_yet_submitted()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction", ownerToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSeasonGoalPrediction_is_readable_by_an_active_League_member_who_does_not_own_the_FantasyTeam()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));
        var path = $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction";
        await client.SendAsync(Request(HttpMethod.Put, path, ownerToken, new { predictedEplGoals = 1234 }));

        // A second user joins the same League via the real invitation flow (IT-04/IT-05) — an
        // active member, but not the owner of this FantasyTeam.
        var createInvitationResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/invitations", ownerToken, new { destination = "other@example.com", channel = "Email" }));
        var invitation = await createInvitationResponse.Content.ReadFromJsonAsync<InvitationDto>();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var rawToken = (await db.Invitations.SingleAsync(i => i.InvitationId == invitation!.InvitationId)).Token;
        var memberToken = await RegisterAsync(client);
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/invitations/{rawToken}/accept", memberToken));

        var response = await client.SendAsync(Request(HttpMethod.Get, path, memberToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetSeasonGoalPrediction_is_rejected_for_an_outsider()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));
        var path = $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction";
        await client.SendAsync(Request(HttpMethod.Put, path, ownerToken, new { predictedEplGoals = 1234 }));
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, path, outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SubmitSeasonGoalPrediction_is_rejected_for_a_caller_who_does_not_own_the_FantasyTeam()
    {
        var client = factory.CreateClient();
        var (_, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));
        var otherToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction", otherToken, new { predictedEplGoals = 1234 }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SubmitSeasonGoalPrediction_after_Season_start_locks_immediately_and_a_second_attempt_returns_409()
    {
        var client = factory.CreateClient();
        // Season started yesterday — the BR-299 late-submission fallback.
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
        var path = $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction";

        var firstResponse = await client.SendAsync(Request(HttpMethod.Put, path, ownerToken, new { predictedEplGoals = 1234 }));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        var secondResponse = await client.SendAsync(Request(HttpMethod.Put, path, ownerToken, new { predictedEplGoals = 9999 }));

        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
        var body = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("season_goal_prediction_locked", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SubmitSeasonGoalPrediction_with_a_negative_value_is_rejected_by_DTO_level_validation()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));

        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction", ownerToken, new { predictedEplGoals = -1 }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
