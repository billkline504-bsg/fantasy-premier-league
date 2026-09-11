using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.Competition;
using EplFantasy.FantasyTeams;
using EplFantasy.Identity;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-41 (F-009.4, BR-218-BR-220): proves getSchedule/getMatch end-to-end against the real host —
/// contract conformance only, per the task breakdown's own "Domain: none new" (every field these
/// endpoints expose is already populated by IT-38/IT-39/IT-40's own cascade). No schedule-generation
/// endpoint exists yet (ScheduleGenerationService is an internal-only service, IT-38), so each test
/// seeds its own HeadToHeadMatch row(s) directly.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class CompetitionControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    private async Task<(string OwnerToken, Guid LeagueId, Guid SeasonId, string EplSeasonIdentifier)> SeedLeagueAndSeasonAsync(HttpClient client)
    {
        var ownerToken = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "Competition API Test League" }));
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

        return (ownerToken, league.LeagueId, season!.SeasonId, eplSeasonIdentifier);
    }

    /// <summary>A real Gameweek row — HeadToHeadMatch.GameweekId is a genuine foreign key, not just an opaque id.</summary>
    private async Task<Guid> SeedGameweekAsync(string eplSeasonIdentifier, int number)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = number, RosterLockDeadline = DateTimeOffset.UtcNow.AddDays(number * 7) };
        db.Gameweeks.Add(gameweek);
        await db.SaveChangesAsync();
        return gameweek.GameweekId;
    }

    /// <summary>A real FantasyTeam (with its own User/LeagueMembership) — HeadToHeadMatch.Home/AwayFantasyTeamId are genuine foreign keys, not just opaque ids.</summary>
    private async Task<Guid> SeedFantasyTeamAsync(Guid leagueId, Guid seasonId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        db.Users.Add(new User { UserId = userId, Username = $"member{suffix}", Email = $"member{suffix}@example.com", PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var membership = LeagueMembership.Join(Guid.NewGuid(), leagueId, userId, DateTimeOffset.UtcNow);
        db.LeagueMemberships.Add(membership);

        var fantasyTeamId = Guid.NewGuid();
        db.FantasyTeams.Add(new FantasyTeam
        {
            FantasyTeamId = fantasyTeamId,
            LeagueMembershipId = membership.LeagueMembershipId,
            SeasonId = seasonId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return fantasyTeamId;
    }

    private async Task<Guid> SeedMatchAsync(Guid seasonId, Guid gameweekId, HeadToHeadMatch? match = null, Guid? homeFantasyTeamId = null, Guid? awayFantasyTeamId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        match ??= HeadToHeadMatch.Schedule(Guid.NewGuid(), seasonId, gameweekId, homeFantasyTeamId!.Value, awayFantasyTeamId!.Value);
        db.HeadToHeadMatches.Add(match);
        await db.SaveChangesAsync();
        return match.MatchId;
    }

    private async Task SeedStandingAsync(Guid seasonId, Guid fantasyTeamId, Guid asOfGameweekId, int position, int leaguePoints = 0)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var standing = LeagueStanding.Calculate(
            seasonId, fantasyTeamId, asOfGameweekId,
            leaguePoints, played: 1, won: 1, drawn: 0, lost: 0,
            fantasyGoalsFor: 10, fantasyGoalsAgainst: 5, fantasyGoalDifference: 5, captainPointsTotal: 20);
        standing.AssignPosition(position);
        db.LeagueStandings.Add(standing);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetSchedule_returns_every_scheduled_match_for_the_season()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, eplSeasonIdentifier) = await SeedLeagueAndSeasonAsync(client);
        var gameweek1 = await SeedGameweekAsync(eplSeasonIdentifier, 1);
        var gameweek2 = await SeedGameweekAsync(eplSeasonIdentifier, 2);
        var home = await SeedFantasyTeamAsync(leagueId, seasonId);
        var away = await SeedFantasyTeamAsync(leagueId, seasonId);
        await SeedMatchAsync(seasonId, gameweek1, homeFantasyTeamId: home, awayFantasyTeamId: away);
        await SeedMatchAsync(seasonId, gameweek2, homeFantasyTeamId: home, awayFantasyTeamId: away);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/schedule", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var matches = await response.Content.ReadFromJsonAsync<List<HeadToHeadMatchDto>>();
        Assert.Equal(2, matches!.Count);
    }

    [Fact]
    public async Task GetSchedule_filters_by_the_gameweekId_query_parameter()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, eplSeasonIdentifier) = await SeedLeagueAndSeasonAsync(client);
        var gameweek1 = await SeedGameweekAsync(eplSeasonIdentifier, 1);
        var gameweek2 = await SeedGameweekAsync(eplSeasonIdentifier, 2);
        var home = await SeedFantasyTeamAsync(leagueId, seasonId);
        var away = await SeedFantasyTeamAsync(leagueId, seasonId);
        await SeedMatchAsync(seasonId, gameweek1, homeFantasyTeamId: home, awayFantasyTeamId: away);
        await SeedMatchAsync(seasonId, gameweek2, homeFantasyTeamId: home, awayFantasyTeamId: away);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/schedule?gameweekId={gameweek1}", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var matches = await response.Content.ReadFromJsonAsync<List<HeadToHeadMatchDto>>();
        Assert.Single(matches!, m => m.GameweekId == gameweek1);
    }

    [Fact]
    public async Task GetSchedule_is_rejected_for_a_caller_who_is_not_an_active_member()
    {
        var client = factory.CreateClient();
        var (_, leagueId, seasonId, _) = await SeedLeagueAndSeasonAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/schedule", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetMatch_returns_the_matchs_full_result_and_league_points_detail()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, eplSeasonIdentifier) = await SeedLeagueAndSeasonAsync(client);
        var gameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1);
        var homeFantasyTeamId = await SeedFantasyTeamAsync(leagueId, seasonId);
        var awayFantasyTeamId = await SeedFantasyTeamAsync(leagueId, seasonId);
        var match = HeadToHeadMatch.Schedule(Guid.NewGuid(), seasonId, gameweekId, homeFantasyTeamId, awayFantasyTeamId);
        match.CalculateResult(homeFantasyPoints: 60, awayFantasyPoints: 45);
        match.AllocateLeaguePoints(win: 3, draw: 1, loss: 0);
        var matchId = await SeedMatchAsync(seasonId, match.GameweekId, match);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/matches/{matchId}", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<HeadToHeadMatchDto>();
        Assert.Equal(matchId, dto!.MatchId);
        Assert.Equal(homeFantasyTeamId, dto.HomeFantasyTeamId);
        Assert.Equal(awayFantasyTeamId, dto.AwayFantasyTeamId);
        Assert.Equal(60, dto.HomeScore);
        Assert.Equal(45, dto.AwayScore);
        Assert.Equal("HomeWin", dto.Result);
        Assert.Equal(3, dto.LeaguePointsHome);
        Assert.Equal(0, dto.LeaguePointsAway);
    }

    [Fact]
    public async Task GetMatch_returns_a_not_yet_played_match_with_every_score_field_null()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, eplSeasonIdentifier) = await SeedLeagueAndSeasonAsync(client);
        var gameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1);
        var home = await SeedFantasyTeamAsync(leagueId, seasonId);
        var away = await SeedFantasyTeamAsync(leagueId, seasonId);
        var matchId = await SeedMatchAsync(seasonId, gameweekId, homeFantasyTeamId: home, awayFantasyTeamId: away);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/matches/{matchId}", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<HeadToHeadMatchDto>();
        Assert.Null(dto!.HomeScore);
        Assert.Null(dto.AwayScore);
        Assert.Null(dto.Result);
        Assert.Null(dto.LeaguePointsHome);
        Assert.Null(dto.LeaguePointsAway);
    }

    [Fact]
    public async Task GetMatch_returns_404_for_an_unknown_matchId()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, _) = await SeedLeagueAndSeasonAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/matches/{Guid.NewGuid()}", ownerToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetMatch_is_rejected_for_a_caller_who_is_not_an_active_member()
    {
        var client = factory.CreateClient();
        var (_, leagueId, seasonId, eplSeasonIdentifier) = await SeedLeagueAndSeasonAsync(client);
        var gameweekId = await SeedGameweekAsync(eplSeasonIdentifier, 1);
        var home = await SeedFantasyTeamAsync(leagueId, seasonId);
        var away = await SeedFantasyTeamAsync(leagueId, seasonId);
        var matchId = await SeedMatchAsync(seasonId, gameweekId, homeFantasyTeamId: home, awayFantasyTeamId: away);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/matches/{matchId}", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetStandings_with_no_asOfGameweekId_returns_the_latest_computed_snapshot_ordered_by_Position()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, eplSeasonIdentifier) = await SeedLeagueAndSeasonAsync(client);
        var gameweek1 = await SeedGameweekAsync(eplSeasonIdentifier, 1);
        var gameweek2 = await SeedGameweekAsync(eplSeasonIdentifier, 2);
        var teamA = await SeedFantasyTeamAsync(leagueId, seasonId);
        var teamB = await SeedFantasyTeamAsync(leagueId, seasonId);
        // Gameweek 1's own (now stale) snapshot has A first; Gameweek 2's (the latest) has B first.
        await SeedStandingAsync(seasonId, teamA, gameweek1, position: 1);
        await SeedStandingAsync(seasonId, teamB, gameweek1, position: 2);
        await SeedStandingAsync(seasonId, teamB, gameweek2, position: 1);
        await SeedStandingAsync(seasonId, teamA, gameweek2, position: 2);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/standings", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var standings = await response.Content.ReadFromJsonAsync<List<LeagueStandingDto>>();
        Assert.Equal(2, standings!.Count);
        Assert.All(standings, s => Assert.Equal(gameweek2, s.AsOfGameweekId));
        Assert.Equal(teamB, standings[0].FantasyTeamId); // Position 1.
        Assert.Equal(teamA, standings[1].FantasyTeamId); // Position 2.
    }

    [Fact]
    public async Task GetStandings_with_an_explicit_asOfGameweekId_returns_that_historical_snapshot()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, eplSeasonIdentifier) = await SeedLeagueAndSeasonAsync(client);
        var gameweek1 = await SeedGameweekAsync(eplSeasonIdentifier, 1);
        var gameweek2 = await SeedGameweekAsync(eplSeasonIdentifier, 2);
        var team = await SeedFantasyTeamAsync(leagueId, seasonId);
        await SeedStandingAsync(seasonId, team, gameweek1, position: 1, leaguePoints: 3);
        await SeedStandingAsync(seasonId, team, gameweek2, position: 1, leaguePoints: 6);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/standings?asOfGameweekId={gameweek1}", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var standings = await response.Content.ReadFromJsonAsync<List<LeagueStandingDto>>();
        var standing = Assert.Single(standings!);
        Assert.Equal(gameweek1, standing.AsOfGameweekId);
        Assert.Equal(3, standing.LeaguePoints); // GW1's own value, not GW2's later 6.
    }

    [Fact]
    public async Task GetStandings_returns_an_empty_list_when_no_snapshot_has_ever_been_computed()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, _) = await SeedLeagueAndSeasonAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/standings", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var standings = await response.Content.ReadFromJsonAsync<List<LeagueStandingDto>>();
        Assert.Empty(standings!);
    }

    [Fact]
    public async Task GetStandings_is_rejected_for_a_caller_who_is_not_an_active_member()
    {
        var client = factory.CreateClient();
        var (_, leagueId, seasonId, _) = await SeedLeagueAndSeasonAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/standings", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
