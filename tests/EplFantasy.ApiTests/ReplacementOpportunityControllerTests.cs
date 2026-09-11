using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.Drafts;
using EplFantasy.FantasyTeams;
using EplFantasy.Infrastructure;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-48 (F-006.4, BR-063-BR-068/BR-260-BR-264/BR-287): proves listReplacementOpportunities
/// (owner-or-League-Administrator) and the spend action (owner-only) end-to-end against the real
/// host. No grant endpoint exists yet (IT-21's automatic EPL-exit path and IT-49's not-yet-built
/// Administrator injury-declaration path are the only two ways an opportunity is ever created), so
/// each test seeds its own ReplacementOpportunity row directly.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class ReplacementOpportunityControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    private const string StrongPassword = "correct-horse-battery-staple-97!";

    private async Task<(string Token, Guid UserId)> RegisterAsync(HttpClient client)
    {
        var username = $"u{Guid.NewGuid():N}"[..16];
        var email = $"{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });
        var tokens = await response.Content.ReadFromJsonAsync<AuthTokenResponse>();

        var meResponse = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/users/me", tokens!.AccessToken));
        var me = await meResponse.Content.ReadFromJsonAsync<UserSelfDto>();

        return (tokens.AccessToken, me!.UserId);
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

    /// <summary>Seeds a League (owner = Administrator) + Season with two FantasyTeams: the owner's own, and a second member's.</summary>
    private async Task<(string OwnerToken, string MemberToken, Guid LeagueId, Guid SeasonId, Guid OwnerFantasyTeamId, Guid MemberFantasyTeamId)> SeedLeagueWithTwoFantasyTeamsAsync(HttpClient client)
    {
        var (ownerToken, _) = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = $"Replacement League {Guid.NewGuid():N}" }));
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

        var ownerTeamResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons/{season!.SeasonId}/fantasy-teams", ownerToken));
        var ownerTeam = await ownerTeamResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();

        var (memberToken, memberUserId) = await RegisterAsync(client);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.LeagueMemberships.Add(LeagueMembership.Join(Guid.NewGuid(), league.LeagueId, memberUserId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var memberTeamResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{league.LeagueId}/seasons/{season.SeasonId}/fantasy-teams", memberToken));
        var memberTeam = await memberTeamResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();

        return (ownerToken, memberToken, league.LeagueId, season.SeasonId, ownerTeam!.FantasyTeamId, memberTeam!.FantasyTeamId);
    }

    private async Task<Guid> SeedReplacementOpportunityAsync(Guid fantasyTeamId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var sourcePlayerId = Guid.NewGuid();
        db.Players.Add(new Player { PlayerId = sourcePlayerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Exited Player", Position = PlayerPosition.Mid });
        var opportunity = new ReplacementOpportunity
        {
            ReplacementOpportunityId = Guid.NewGuid(),
            FantasyTeamId = fantasyTeamId,
            SourcePlayerId = sourcePlayerId,
            GrantedAt = DateTimeOffset.UtcNow,
            GrantReason = ReplacementGrantReason.EplExit,
            SpentAt = null,
        };
        db.ReplacementOpportunities.Add(opportunity);
        await db.SaveChangesAsync();
        return opportunity.ReplacementOpportunityId;
    }

    private async Task<Guid> SeedPlayerAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var player = new Player { PlayerId = Guid.NewGuid(), EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Replacement Candidate", Position = PlayerPosition.Fwd };
        db.Players.Add(player);
        await db.SaveChangesAsync();
        return player.PlayerId;
    }

    [Fact]
    public async Task ListReplacementOpportunities_returns_the_FantasyTeams_own_unspent_opportunities()
    {
        var client = factory.CreateClient();
        var (ownerToken, _, leagueId, seasonId, ownerFantasyTeamId, _) = await SeedLeagueWithTwoFantasyTeamsAsync(client);
        var opportunityId = await SeedReplacementOpportunityAsync(ownerFantasyTeamId);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{ownerFantasyTeamId}/replacement-opportunities", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var opportunities = await response.Content.ReadFromJsonAsync<List<ReplacementOpportunityDto>>();
        var opportunity = Assert.Single(opportunities!);
        Assert.Equal(opportunityId, opportunity.OpportunityId);
        Assert.Null(opportunity.SpentAt);
        Assert.Equal("EplExit", opportunity.GrantReason);
    }

    [Fact]
    public async Task ListReplacementOpportunities_is_readable_by_the_League_Administrator_for_another_teams_opportunities()
    {
        var client = factory.CreateClient();
        var (ownerToken, _, leagueId, seasonId, _, memberFantasyTeamId) = await SeedLeagueWithTwoFantasyTeamsAsync(client);
        await SeedReplacementOpportunityAsync(memberFantasyTeamId);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{memberFantasyTeamId}/replacement-opportunities", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var opportunities = await response.Content.ReadFromJsonAsync<List<ReplacementOpportunityDto>>();
        Assert.Single(opportunities!);
    }

    [Fact]
    public async Task ListReplacementOpportunities_is_rejected_for_an_outsider()
    {
        var client = factory.CreateClient();
        var (_, _, leagueId, seasonId, ownerFantasyTeamId, _) = await SeedLeagueWithTwoFantasyTeamsAsync(client);
        var (outsiderToken, _) = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{ownerFantasyTeamId}/replacement-opportunities", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MakeReplacementPick_spends_the_opportunity_and_records_AcquisitionType_Replacement()
    {
        var client = factory.CreateClient();
        var (ownerToken, _, leagueId, seasonId, ownerFantasyTeamId, _) = await SeedLeagueWithTwoFantasyTeamsAsync(client);
        var opportunityId = await SeedReplacementOpportunityAsync(ownerFantasyTeamId);
        var replacementPlayerId = await SeedPlayerAsync();

        var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{ownerFantasyTeamId}/replacement-opportunities/{opportunityId}/spend",
            ownerToken,
            new { playerId = replacementPlayerId }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ReplacementOpportunityDto>();
        Assert.NotNull(dto!.SpentAt);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var squadPlayer = await db.SquadPlayers.SingleAsync(sp => sp.FantasyTeamId == ownerFantasyTeamId && sp.PlayerId == replacementPlayerId);
        Assert.Equal(AcquisitionType.Replacement, squadPlayer.AcquisitionType);
    }

    [Fact]
    public async Task MakeReplacementPick_is_rejected_for_a_caller_who_does_not_own_the_FantasyTeam()
    {
        var client = factory.CreateClient();
        var (_, memberToken, leagueId, seasonId, ownerFantasyTeamId, _) = await SeedLeagueWithTwoFantasyTeamsAsync(client);
        var opportunityId = await SeedReplacementOpportunityAsync(ownerFantasyTeamId);
        var replacementPlayerId = await SeedPlayerAsync();

        var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{ownerFantasyTeamId}/replacement-opportunities/{opportunityId}/spend",
            memberToken,
            new { playerId = replacementPlayerId }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MakeReplacementPick_returns_409_when_the_opportunity_is_already_spent()
    {
        var client = factory.CreateClient();
        var (ownerToken, _, leagueId, seasonId, ownerFantasyTeamId, _) = await SeedLeagueWithTwoFantasyTeamsAsync(client);
        var opportunityId = await SeedReplacementOpportunityAsync(ownerFantasyTeamId);
        var path = $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{ownerFantasyTeamId}/replacement-opportunities/{opportunityId}/spend";
        var firstPlayerId = await SeedPlayerAsync();
        var secondPlayerId = await SeedPlayerAsync();
        await client.SendAsync(Request(HttpMethod.Post, path, ownerToken, new { playerId = firstPlayerId }));

        var response = await client.SendAsync(Request(HttpMethod.Post, path, ownerToken, new { playerId = secondPlayerId }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("replacement_opportunity_already_spent", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task MakeReplacementPick_returns_409_when_the_player_is_already_owned_by_another_FantasyTeam()
    {
        var client = factory.CreateClient();
        var (ownerToken, memberToken, leagueId, seasonId, ownerFantasyTeamId, memberFantasyTeamId) = await SeedLeagueWithTwoFantasyTeamsAsync(client);
        var opportunityId = await SeedReplacementOpportunityAsync(ownerFantasyTeamId);
        var contestedPlayerId = await SeedPlayerAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            db.SquadPlayers.Add(new SquadPlayer
            {
                SquadPlayerId = Guid.NewGuid(),
                FantasyTeamId = memberFantasyTeamId,
                PlayerId = contestedPlayerId,
                SeasonId = seasonId,
                AcquisitionType = AcquisitionType.InitialDraft,
                AcquiredAt = DateTimeOffset.UtcNow,
                IsCurrentlyOwned = true,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{ownerFantasyTeamId}/replacement-opportunities/{opportunityId}/spend",
            ownerToken,
            new { playerId = contestedPlayerId }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("player_already_owned", body.GetProperty("errorCode").GetString());
    }
}
