using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.FantasyTeams;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// Proves IT-29's getGameweekRoster/submitGameweekRoster, IT-30's setCaptain, and IT-32's
/// correctRoster (a separate AdminRosterController) end-to-end: 404 before any submission,
/// FantasyTeamOwner/FantasyTeamOwnerOrActiveLeagueMember/RosterLeagueAdministrator's own 401/403,
/// a valid submission's 200 plus ETag header, BR-279's 400 invalid_roster_composition, BR-299's
/// 409 season_goal_prediction_required, Architecture §8.3's 409 on a stale If-Match, BR-046's 400
/// when setCaptain's captainPlayerId isn't one of the roster's selected players, and BR-146's 409
/// when correctRoster is attempted against a roster that isn't Locked (or Scored) yet.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class RosterControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    /// <summary>Seeds a League/Season/FantasyTeam through the real API, shrinks WeeklyRosterSize/PositionalMinimums to 4/(1,1,1,1) (the same small-deterministic-size precedent RosterServiceTests established), and seeds a Gameweek plus exactly 4 currently-owned SquadPlayers (one per BR-279 category) directly via the DbContext (no creation API exists for either).</summary>
    private async Task<(string OwnerToken, Guid LeagueId, Guid SeasonId, Guid FantasyTeamId, Guid GameweekId, List<Guid> OwnedPlayerIds)> SeedRosterPrerequisitesAsync(HttpClient client)
    {
        var ownerToken = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = $"Roster League {Guid.NewGuid():N}" }));
        var league = await leagueResponse.Content.ReadFromJsonAsync<LeagueDto>();

        var eplSeasonIdentifier = $"season-{Guid.NewGuid():N}"[..16];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
            await db.SaveChangesAsync();
        }

        var seasonResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league!.LeagueId}/seasons", ownerToken, new { eplSeasonIdentifier, startDate = "2026-08-15" }));
        var season = await seasonResponse.Content.ReadFromJsonAsync<SeasonDto>();

        var teamResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons/{season!.SeasonId}/fantasy-teams", ownerToken));
        var team = await teamResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();

        Guid gameweekId;
        var ownedPlayerIds = new List<Guid>();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

            var seasonConfiguration = await db.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == season.SeasonId);
            seasonConfiguration.WeeklyRosterSize = 4;
            seasonConfiguration.PositionalMinimumGk = 1;
            seasonConfiguration.PositionalMinimumDef = 1;
            seasonConfiguration.PositionalMinimumMid = 1;
            seasonConfiguration.PositionalMinimumFwd = 1;

            var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = DateTimeOffset.UtcNow.AddDays(7) };
            db.Gameweeks.Add(gameweek);
            gameweekId = gameweek.GameweekId;

            foreach (var position in new[] { PlayerPosition.Gk, PlayerPosition.Def, PlayerPosition.Mid, PlayerPosition.Fwd })
            {
                var playerId = Guid.NewGuid();
                db.Players.Add(new Player { PlayerId = playerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = $"{position} Player", Position = position });
                db.SquadPlayers.Add(new SquadPlayer
                {
                    SquadPlayerId = Guid.NewGuid(),
                    FantasyTeamId = team!.FantasyTeamId,
                    PlayerId = playerId,
                    SeasonId = season.SeasonId,
                    AcquisitionType = AcquisitionType.InitialDraft,
                    AcquiredAt = DateTimeOffset.UtcNow,
                    IsCurrentlyOwned = true,
                });
                ownedPlayerIds.Add(playerId);
            }

            await db.SaveChangesAsync();
        }

        return (ownerToken, league.LeagueId, season.SeasonId, team!.FantasyTeamId, gameweekId, ownedPlayerIds);
    }

    private async Task SubmitSeasonGoalPredictionAsync(HttpClient client, string ownerToken, Guid leagueId, Guid seasonId, Guid fantasyTeamId)
    {
        var response = await client.SendAsync(Request(
            HttpMethod.Put,
            $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/season-goal-prediction",
            ownerToken,
            new { predictedEplGoals = 1000 }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetGameweekRoster_returns_404_when_no_roster_has_been_submitted_yet()
    {
        var client = factory.CreateClient();
        var (ownerToken, _, _, fantasyTeamId, gameweekId, _) = await SeedRosterPrerequisitesAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetGameweekRoster_is_rejected_for_a_caller_who_is_neither_the_owner_nor_a_League_member()
    {
        var client = factory.CreateClient();
        var (_, _, _, fantasyTeamId, gameweekId, _) = await SeedRosterPrerequisitesAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SubmitGameweekRoster_without_a_bearer_token_returns_401()
    {
        var client = factory.CreateClient();
        var (_, _, _, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);

        var response = await factory.CreateClient().SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", body: new { playerIds = ownedPlayerIds }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SubmitGameweekRoster_is_rejected_for_a_caller_who_does_not_own_the_FantasyTeam()
    {
        var client = factory.CreateClient();
        var (_, _, _, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", outsiderToken, new { playerIds = ownedPlayerIds }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SubmitGameweekRoster_rejects_the_first_submission_of_the_Season_without_a_prediction()
    {
        var client = factory.CreateClient();
        var (ownerToken, _, _, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);

        var response = await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("season_goal_prediction_required", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SubmitGameweekRoster_rejects_an_invalid_composition_with_400()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        await SubmitSeasonGoalPredictionAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);

        // Only 3 of the 4 required players — an undersized submission.
        var response = await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds.Take(3) }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_roster_composition", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SubmitGameweekRoster_with_a_valid_composition_returns_200_with_an_ETag()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        await SubmitSeasonGoalPredictionAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);

        var response = await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken,
            new { playerIds = ownedPlayerIds, captainPlayerId = ownedPlayerIds[0] }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var roster = await response.Content.ReadFromJsonAsync<GameweekRosterDto>();
        Assert.Equal("Submitted", roster!.Status);
        Assert.Equal(ownedPlayerIds[0], roster.CaptainPlayerId);
        Assert.Equal(4, roster.Players.Count);

        var getResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResponse.Headers.ETag is not null);
    }

    [Fact]
    public async Task SubmitGameweekRoster_with_a_stale_If_Match_returns_409()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        await SubmitSeasonGoalPredictionAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);
        await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds }));

        var request = Request(HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds });
        request.Headers.Add("If-Match", "\"999999999\"");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("roster_concurrency_conflict", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SetCaptain_with_a_player_on_the_roster_returns_200()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        await SubmitSeasonGoalPredictionAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);
        await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds }));

        var response = await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster/captain", ownerToken,
            new { captainPlayerId = ownedPlayerIds[1] }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var roster = await response.Content.ReadFromJsonAsync<GameweekRosterDto>();
        Assert.Equal(ownedPlayerIds[1], roster!.CaptainPlayerId);
        Assert.True(roster.Players.Single(p => p.PlayerId == ownedPlayerIds[1]).IsCaptain);
    }

    [Fact]
    public async Task SetCaptain_rejects_a_player_not_on_the_roster_with_400()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        await SubmitSeasonGoalPredictionAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);
        await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds }));

        var response = await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster/captain", ownerToken,
            new { captainPlayerId = Guid.NewGuid() }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_roster_composition", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task SetCaptain_is_rejected_for_a_caller_who_does_not_own_the_FantasyTeam()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        await SubmitSeasonGoalPredictionAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);
        await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds }));
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster/captain", outsiderToken,
            new { captainPlayerId = ownedPlayerIds[0] }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Simulates IT-31's RosterLockSweepHandler having already locked this roster (proven separately by RosterLockSweepHandlerTests) — this suite only needs a Locked roster to correct, not the sweep itself. Returns the roster's GameweekRosterId.</summary>
    private async Task<Guid> LockRosterAsync(Guid fantasyTeamId, Guid gameweekId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var roster = await db.GameweekRosters.SingleAsync(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId);
        roster.Lock(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return roster.GameweekRosterId;
    }

    [Fact]
    public async Task CorrectRoster_by_the_League_Administrator_returns_200_with_the_corrected_roster()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        await SubmitSeasonGoalPredictionAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);
        await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds }));
        var gameweekRosterId = await LockRosterAsync(fantasyTeamId, gameweekId);

        var response = await client.SendAsync(Request(
            HttpMethod.Post, $"/api/v1/admin/rosters/{gameweekRosterId}/correct", ownerToken,
            new { captainPlayerId = ownedPlayerIds[1], reason = "Owner accidentally selected the wrong Captain" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var roster = await response.Content.ReadFromJsonAsync<GameweekRosterDto>();
        Assert.Equal("Locked", roster!.Status);
        Assert.Equal(ownedPlayerIds[1], roster.CaptainPlayerId);
        Assert.Equal(ownedPlayerIds.ToHashSet(), roster.Players.Select(p => p.PlayerId).ToHashSet());
    }

    [Fact]
    public async Task CorrectRoster_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        await SubmitSeasonGoalPredictionAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);
        await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds }));
        var gameweekRosterId = await LockRosterAsync(fantasyTeamId, gameweekId);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(
            HttpMethod.Post, $"/api/v1/admin/rosters/{gameweekRosterId}/correct", outsiderToken,
            new { captainPlayerId = ownedPlayerIds[1], reason = "not an admin" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CorrectRoster_without_a_bearer_token_returns_401()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        await SubmitSeasonGoalPredictionAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);
        await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds }));
        var gameweekRosterId = await LockRosterAsync(fantasyTeamId, gameweekId);

        var response = await client.SendAsync(Request(
            HttpMethod.Post, $"/api/v1/admin/rosters/{gameweekRosterId}/correct", body: new { captainPlayerId = ownedPlayerIds[1], reason = "x" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CorrectRoster_rejects_a_roster_that_is_still_Submitted_with_409()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId, gameweekId, ownedPlayerIds) = await SeedRosterPrerequisitesAsync(client);
        await SubmitSeasonGoalPredictionAsync(client, ownerToken, leagueId, seasonId, fantasyTeamId);
        var submitResponse = await client.SendAsync(Request(
            HttpMethod.Put, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster", ownerToken, new { playerIds = ownedPlayerIds }));
        var submitted = await submitResponse.Content.ReadFromJsonAsync<GameweekRosterDto>();

        var response = await client.SendAsync(Request(
            HttpMethod.Post, $"/api/v1/admin/rosters/{submitted!.GameweekRosterId}/correct", ownerToken,
            new { captainPlayerId = ownedPlayerIds[1], reason = "too early" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gameweek_roster_not_locked", body.GetProperty("errorCode").GetString());
    }
}
