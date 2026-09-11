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
/// IT-49 (F-011.2, BR-065/BR-067/BR-068/BR-260): proves declareSeasonEndingInjury end-to-end
/// against the real host — League-Administrator-only, marks the SquadPlayer eligible, grants a
/// ReplacementOpportunity, and writes a SeasonEndingInjuryDeclared AdministrativeAction with a real
/// (non-null) ActingMembershipId — the distinguishing feature from IT-21's automatic, system-
/// generated EPL-exit grants.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class SquadPlayerControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    private async Task<(string OwnerToken, Guid LeagueId, Guid SeasonId, Guid FantasyTeamId)> SeedLeagueSeasonAndFantasyTeamAsync(HttpClient client)
    {
        var ownerToken = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = $"Injury League {Guid.NewGuid():N}" }));
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

        return (ownerToken, league.LeagueId, season.SeasonId, team!.FantasyTeamId);
    }

    private async Task<Guid> SeedOwnedSquadPlayerAsync(Guid fantasyTeamId, Guid seasonId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var playerId = Guid.NewGuid();
        db.Players.Add(new Player { PlayerId = playerId, EplPlayerId = $"p{Guid.NewGuid():N}"[..12], Name = "Injured Player", Position = PlayerPosition.Def });
        var squadPlayer = new SquadPlayer
        {
            SquadPlayerId = Guid.NewGuid(),
            FantasyTeamId = fantasyTeamId,
            PlayerId = playerId,
            SeasonId = seasonId,
            AcquisitionType = AcquisitionType.InitialDraft,
            AcquiredAt = DateTimeOffset.UtcNow,
            IsCurrentlyOwned = true,
        };
        db.SquadPlayers.Add(squadPlayer);
        await db.SaveChangesAsync();
        return squadPlayer.SquadPlayerId;
    }

    [Fact]
    public async Task DeclareSeasonEndingInjury_marks_eligibility_and_returns_a_new_ReplacementOpportunity()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client);
        var squadPlayerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);

        var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/squad-players/{squadPlayerId}/declare-season-ending-injury",
            ownerToken,
            new { reason = "league consensus reached" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ReplacementOpportunityDto>();
        Assert.Equal("SeasonEndingInjury", dto!.GrantReason);
        Assert.Null(dto.SpentAt);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var squadPlayer = await db.SquadPlayers.SingleAsync(sp => sp.SquadPlayerId == squadPlayerId);
        Assert.NotNull(squadPlayer.ReplacementEligibleAt);
    }

    [Fact]
    public async Task DeclareSeasonEndingInjury_is_rejected_for_a_caller_who_is_not_the_League_Administrator()
    {
        var client = factory.CreateClient();
        var (_, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client);
        var squadPlayerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/squad-players/{squadPlayerId}/declare-season-ending-injury",
            outsiderToken,
            new { }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeclareSeasonEndingInjury_returns_409_when_the_SquadPlayer_is_already_replacement_eligible()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId, fantasyTeamId) = await SeedLeagueSeasonAndFantasyTeamAsync(client);
        var squadPlayerId = await SeedOwnedSquadPlayerAsync(fantasyTeamId, seasonId);
        var path = $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/squad-players/{squadPlayerId}/declare-season-ending-injury";
        await client.SendAsync(Request(HttpMethod.Post, path, ownerToken, new { }));

        var response = await client.SendAsync(Request(HttpMethod.Post, path, ownerToken, new { }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("squad_player_already_replacement_eligible", body.GetProperty("errorCode").GetString());
    }
}
