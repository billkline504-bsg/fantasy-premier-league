using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.FantasyTeams;
using EplFantasy.Infrastructure;
using EplFantasy.PlayerData;
using EplFantasy.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// Proves IT-33's getGameweekScore end-to-end: 404 before any GameweekScore exists, the
/// FantasyTeamLeagueMember policy's own 401/403 (no owner shortcut — any active League member, not
/// just the FantasyTeam's own owner, can read its score, per BR-163), and a 200 with the exact
/// GameweekScore shape once one exists. No creation API exists yet for GameweekScore itself
/// (scoring is a background job, IT-33's own "API" note) — each test seeds one directly via the
/// DbContext, the same precedent SeasonGoalPredictionControllerTests already established for
/// FantasyTeam.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class ScoringControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    /// <summary>Seeds a League (through the real API, so the owner becomes its founding Administrator) plus a FantasyTeam and a Gameweek (the latter directly via the DbContext — no creation API exists for it).</summary>
    private async Task<(string OwnerToken, Guid LeagueId, Guid FantasyTeamId, Guid GameweekId)> SeedScoringPrerequisitesAsync(HttpClient client)
    {
        var ownerToken = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = $"Scoring League {Guid.NewGuid():N}" }));
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
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            var gameweek = new Gameweek { GameweekId = Guid.NewGuid(), EplSeasonIdentifier = eplSeasonIdentifier, Number = 1, RosterLockDeadline = DateTimeOffset.UtcNow.AddDays(7) };
            db.Gameweeks.Add(gameweek);
            await db.SaveChangesAsync();
            gameweekId = gameweek.GameweekId;
        }

        return (ownerToken, league.LeagueId, team!.FantasyTeamId, gameweekId);
    }

    /// <summary>Invites and accepts a brand-new User into the League as a plain (non-Administrator) active member — the same real createInvitation/acceptInvitation round trip SeasonGoalPredictionControllerTests already established.</summary>
    private async Task<string> InviteAndAcceptAsync(HttpClient client, string ownerToken, Guid leagueId)
    {
        var createInvitationResponse = await client.SendAsync(Request(
            HttpMethod.Post, $"/api/v1/leagues/{leagueId}/invitations", ownerToken, new { destination = "other@example.com", channel = "Email" }));
        var invitation = await createInvitationResponse.Content.ReadFromJsonAsync<InvitationDto>();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var rawToken = (await db.Invitations.SingleAsync(i => i.InvitationId == invitation!.InvitationId)).Token;

        var memberToken = await RegisterAsync(client);
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/invitations/{rawToken}/accept", memberToken));

        return memberToken;
    }

    private async Task SeedGameweekScoreAsync(Guid fantasyTeamId, Guid gameweekId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        db.GameweekScores.Add(GameweekScore.Calculate(
            Guid.NewGuid(), fantasyTeamId, gameweekId,
            fantasyPoints: 11, captainPoints: 9, fantasyGoalsFor: 2, fantasyGoalsAgainst: 1, fantasyGoalDifference: 1, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetGameweekScore_returns_404_before_any_score_exists()
    {
        var client = factory.CreateClient();
        var (ownerToken, _, fantasyTeamId, gameweekId) = await SeedScoringPrerequisitesAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score", ownerToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetGameweekScore_returns_200_with_the_calculated_score()
    {
        var client = factory.CreateClient();
        var (ownerToken, _, fantasyTeamId, gameweekId) = await SeedScoringPrerequisitesAsync(client);
        await SeedGameweekScoreAsync(fantasyTeamId, gameweekId);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var score = await response.Content.ReadFromJsonAsync<GameweekScoreDto>();
        Assert.Equal(fantasyTeamId, score!.FantasyTeamId);
        Assert.Equal(gameweekId, score.GameweekId);
        Assert.Equal(11, score.FantasyPoints);
        Assert.Equal(9, score.CaptainPoints);
        Assert.Equal(2, score.FantasyGoalsFor);
        Assert.Equal(1, score.FantasyGoalsAgainst);
        Assert.Equal(1, score.FantasyGoalDifference);
    }

    [Fact]
    public async Task GetGameweekScore_is_readable_by_an_active_League_member_who_does_not_own_the_FantasyTeam()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, fantasyTeamId, gameweekId) = await SeedScoringPrerequisitesAsync(client);
        await SeedGameweekScoreAsync(fantasyTeamId, gameweekId);
        var memberToken = await InviteAndAcceptAsync(client, ownerToken, leagueId);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score", memberToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetGameweekScore_is_rejected_for_a_caller_who_is_not_a_League_member()
    {
        var client = factory.CreateClient();
        var (_, _, fantasyTeamId, gameweekId) = await SeedScoringPrerequisitesAsync(client);
        await SeedGameweekScoreAsync(fantasyTeamId, gameweekId);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetGameweekScore_without_a_bearer_token_returns_401()
    {
        var client = factory.CreateClient();
        var (_, _, fantasyTeamId, gameweekId) = await SeedScoringPrerequisitesAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
