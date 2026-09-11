using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
/// IT-11 (F-002.3): proves createFantasyTeam (any active member, 409 on a second attempt for the
/// same League+Season) and listFantasyTeams/getFantasyTeam (any active member) end-to-end against
/// the real host. Also proves IT-25's getSquad (F-007.5): the complete squad including a released
/// player (BR-264's retained history), position/search/sort (BR-314–BR-316), season statistics
/// defaulting to zero before any official data exists (BR-317), and that only the owning
/// FantasyTeam's own caller may read it (BR-163, resolved in favor of the Feature Behavior Spec's
/// own more specific Security considerations over the OpenAPI spec's plain "active member" note).
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class FantasyTeamControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    private async Task<(string OwnerToken, Guid LeagueId, Guid SeasonId)> SeedLeagueAndSeasonAsync(HttpClient client)
    {
        var ownerToken = await RegisterAsync(client);
        var leagueResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/leagues", ownerToken, new { name = "FantasyTeam Test League" }));
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

        return (ownerToken, league.LeagueId, season!.SeasonId);
    }

    private async Task<SquadPlayer> SeedSquadPlayerAsync(
        Guid fantasyTeamId,
        Guid seasonId,
        string playerName,
        PlayerPosition position = PlayerPosition.Mid,
        AcquisitionType acquisitionType = AcquisitionType.InitialDraft,
        bool isCurrentlyOwned = true,
        DateTimeOffset? replacementEligibleAt = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
        var club = new Club { ClubId = Guid.NewGuid(), EplClubId = $"club{suffix}", Name = $"Zzz FC {suffix}", ShortName = "ZFC" };
        var player = new Player { PlayerId = Guid.NewGuid(), EplPlayerId = $"player{suffix}", Name = playerName, Position = position, CurrentClubId = club.ClubId };
        var squadPlayer = new SquadPlayer
        {
            SquadPlayerId = Guid.NewGuid(),
            FantasyTeamId = fantasyTeamId,
            PlayerId = player.PlayerId,
            SeasonId = seasonId,
            AcquisitionType = acquisitionType,
            AcquiredAt = DateTimeOffset.UtcNow,
            ReleasedAt = isCurrentlyOwned ? null : DateTimeOffset.UtcNow,
            IsCurrentlyOwned = isCurrentlyOwned,
            ReplacementEligibleAt = replacementEligibleAt,
        };
        db.Clubs.Add(club);
        db.Players.Add(player);
        db.SquadPlayers.Add(squadPlayer);
        await db.SaveChangesAsync();

        return squadPlayer;
    }

    [Fact]
    public async Task CreateFantasyTeam_establishes_an_active_team_for_the_caller()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var team = await response.Content.ReadFromJsonAsync<FantasyTeamDto>();
        Assert.Equal(seasonId, team!.SeasonId);
        Assert.Equal("Active", team.Status);
        Assert.NotEmpty(team.Username);
    }

    [Fact]
    public async Task CreateFantasyTeam_is_rejected_for_a_caller_who_is_not_an_active_member()
    {
        var client = factory.CreateClient();
        var (_, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateFantasyTeam_a_second_time_returns_409()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fantasy_team_already_exists", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ListFantasyTeams_and_getFantasyTeam_reflect_a_created_team()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var created = await createResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();

        var listResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<List<FantasyTeamDto>>();
        Assert.Single(list!, t => t.FantasyTeamId == created!.FantasyTeamId);

        var getResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{created!.FantasyTeamId}", ownerToken));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();
        Assert.Equal(created.FantasyTeamId, fetched!.FantasyTeamId);
    }

    [Fact]
    public async Task GetFantasyTeam_is_rejected_for_a_caller_who_is_not_an_active_member()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var created = await createResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();
        var outsiderToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{created!.FantasyTeamId}", outsiderToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetSquad_without_a_bearer_token_returns_401()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var team = await createResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();

        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{team!.FantasyTeamId}/squad"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSquad_is_rejected_for_a_caller_who_does_not_own_the_FantasyTeam()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var team = await createResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();
        var otherToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{team!.FantasyTeamId}/squad", otherToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetSquad_lists_every_SquadPlayer_including_a_released_one()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var team = await createResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();
        var owned = await SeedSquadPlayerAsync(team!.FantasyTeamId, seasonId, "Owned Player");
        var released = await SeedSquadPlayerAsync(team.FantasyTeamId, seasonId, "Released Player", isCurrentlyOwned: false);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{team.FantasyTeamId}/squad", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var squad = await response.Content.ReadFromJsonAsync<List<SquadPlayerViewDto>>();
        Assert.Equal(2, squad!.Count);
        var ownedDto = Assert.Single(squad, s => s.SquadPlayerId == owned.SquadPlayerId);
        Assert.True(ownedDto.IsCurrentlyOwned);
        var releasedDto = Assert.Single(squad, s => s.SquadPlayerId == released.SquadPlayerId);
        Assert.False(releasedDto.IsCurrentlyOwned); // BR-264: still visible, not omitted.
        Assert.False(releasedDto.OnCurrentGameweekRoster); // IT-29 doesn't exist yet — always false for now.
    }

    [Fact]
    public async Task GetSquad_filters_by_position()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var team = await createResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();
        var goalkeeper = await SeedSquadPlayerAsync(team!.FantasyTeamId, seasonId, "GK Player", PlayerPosition.Gk);
        var forward = await SeedSquadPlayerAsync(team.FantasyTeamId, seasonId, "FWD Player", PlayerPosition.Fwd);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{team.FantasyTeamId}/squad?position=Gk", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var squad = await response.Content.ReadFromJsonAsync<List<SquadPlayerViewDto>>();
        Assert.Contains(squad!, s => s.SquadPlayerId == goalkeeper.SquadPlayerId);
        Assert.DoesNotContain(squad!, s => s.SquadPlayerId == forward.SquadPlayerId);
    }

    [Fact]
    public async Task GetSquad_search_is_a_case_insensitive_substring_match()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var team = await createResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();
        var match = await SeedSquadPlayerAsync(team!.FantasyTeamId, seasonId, "Unmistakable Salahsen");
        var nonMatch = await SeedSquadPlayerAsync(team.FantasyTeamId, seasonId, "Someone Else");

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{team.FantasyTeamId}/squad?search=salahsen", ownerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var squad = await response.Content.ReadFromJsonAsync<List<SquadPlayerViewDto>>();
        Assert.Contains(squad!, s => s.SquadPlayerId == match.SquadPlayerId);
        Assert.DoesNotContain(squad!, s => s.SquadPlayerId == nonMatch.SquadPlayerId);
    }

    [Fact]
    public async Task GetSquad_sorts_by_name_and_reverses_with_a_leading_hyphen()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var team = await createResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        await SeedSquadPlayerAsync(team!.FantasyTeamId, seasonId, $"Bravo {suffix}");
        await SeedSquadPlayerAsync(team.FantasyTeamId, seasonId, $"Alpha {suffix}");

        var ascending = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{team.FantasyTeamId}/squad?sort=name&search={suffix}", ownerToken));
        var descending = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{team.FantasyTeamId}/squad?sort=-name&search={suffix}", ownerToken));

        var ascendingSquad = await ascending.Content.ReadFromJsonAsync<List<SquadPlayerViewDto>>();
        var descendingSquad = await descending.Content.ReadFromJsonAsync<List<SquadPlayerViewDto>>();
        Assert.Equal($"Alpha {suffix}", ascendingSquad![0].PlayerName);
        Assert.Equal($"Bravo {suffix}", descendingSquad![0].PlayerName);
    }

    [Fact]
    public async Task GetSquad_shows_AcquisitionType_and_ReplacementEligibleAt_and_defaults_statistics_to_zero()
    {
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var team = await createResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();
        var eligibleAt = DateTimeOffset.UtcNow;
        var squadPlayer = await SeedSquadPlayerAsync(team!.FantasyTeamId, seasonId, "Eligible Player", replacementEligibleAt: eligibleAt);

        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{team.FantasyTeamId}/squad", ownerToken));

        var squad = await response.Content.ReadFromJsonAsync<List<SquadPlayerViewDto>>();
        var dto = Assert.Single(squad!, s => s.SquadPlayerId == squadPlayer.SquadPlayerId);
        Assert.Equal("InitialDraft", dto.AcquisitionType);
        Assert.NotNull(dto.ReplacementEligibleAt);
        // BR-317: no PlayerPerformance rows exist yet for this Season — resolves to zero, not omitted.
        Assert.Equal(0, dto.SeasonMinutesPlayed);
        Assert.Equal(0, dto.SeasonGamesPlayed);
        Assert.Equal(0, dto.SeasonFantasyPoints);
    }

    [Fact]
    public async Task GetFantasyTeam_and_listFantasyTeams_keep_showing_the_username_active_when_the_team_was_created_after_a_later_rename_and_retirement()
    {
        // IT-57 (F-013.2, BR-014/BR-175/BR-272/BR-326): this FantasyTeam's own username must stay
        // pinned to whoever the owner was at Season-1 creation time, even after they rename and
        // then retire — never silently drifting to their later name.
        var client = factory.CreateClient();
        var (ownerToken, leagueId, seasonId) = await SeedLeagueAndSeasonAsync(client);
        var createResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var created = await createResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();
        var originalUsername = created!.Username;

        var renameResponse = await client.SendAsync(Request(HttpMethod.Put, "/api/v1/users/me", ownerToken, new { username = $"renamed{Guid.NewGuid():N}"[..20] }));
        Assert.Equal(HttpStatusCode.OK, renameResponse.StatusCode);
        var retireResponse = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/users/me/retire", ownerToken));
        Assert.Equal(HttpStatusCode.NoContent, retireResponse.StatusCode);

        var getResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{created.FantasyTeamId}", ownerToken));
        var fetched = await getResponse.Content.ReadFromJsonAsync<FantasyTeamDto>();
        Assert.Equal(originalUsername, fetched!.Username);

        var listResponse = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams", ownerToken));
        var list = await listResponse.Content.ReadFromJsonAsync<List<FantasyTeamDto>>();
        Assert.Equal(originalUsername, Assert.Single(list!, t => t.FantasyTeamId == created.FantasyTeamId).Username);
    }
}
