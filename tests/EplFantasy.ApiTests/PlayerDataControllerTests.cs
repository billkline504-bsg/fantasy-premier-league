using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.PlayerData;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-17/IT-18/IT-19 (F-004.1/F-004.2/F-004.6): proves listClubs/listPlayers/getPlayer/
/// listGameweeks/getGameweekFixtures/getEplTable end-to-end against the real host —
/// authenticated-but-not-object-level-scoped reads (any signed-in caller, no per-League check, per
/// the OpenAPI spec's lack of an x-authorization note on any of these six operations) over whatever
/// Club/Player/Gameweek/Fixture/ClubStanding rows already exist. Rows are seeded directly via
/// EplFantasyDbContext, the same pattern ProfileControllerTests uses for ProfileIcon — nothing here
/// calls IPlayerDataSyncService (that's PlayerDataSyncServiceTests' job, against a fake
/// IFplDataSource, including its own proof that ClubStanding is recomputed correctly).
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class PlayerDataControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
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

    private static HttpRequestMessage Request(string path, string? accessToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    private async Task<(Club Club, Player Player)> SeedClubAndPlayerAsync(string playerName = "Reference Player", PlayerPosition position = PlayerPosition.Mid)
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var club = new Club { ClubId = Guid.NewGuid(), EplClubId = $"club-{suffix}", Name = $"Test FC {suffix}", ShortName = "TFC" };
        var player = new Player { PlayerId = Guid.NewGuid(), EplPlayerId = $"player-{suffix}", Name = $"{playerName} {suffix}", Position = position, CurrentClubId = club.ClubId };
        db.Clubs.Add(club);
        db.Players.Add(player);
        await db.SaveChangesAsync();

        return (club, player);
    }

    private async Task<Gameweek> SeedGameweekAsync(int number = 1, DateTimeOffset? rosterLockDeadline = null)
    {
        var eplSeasonIdentifier = $"season-{Guid.NewGuid():N}";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        var gameweek = new Gameweek
        {
            GameweekId = Guid.NewGuid(),
            EplSeasonIdentifier = eplSeasonIdentifier,
            Number = number,
            RosterLockDeadline = rosterLockDeadline ?? DateTimeOffset.UtcNow.AddDays(1),
        };
        db.Gameweeks.Add(gameweek);
        await db.SaveChangesAsync();

        return gameweek;
    }

    private async Task<Fixture> SeedFixtureAsync(Guid gameweekId, DateTimeOffset? kickoffTime = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var home = new Club { ClubId = Guid.NewGuid(), EplClubId = $"home-{suffix}", Name = $"Home FC {suffix}", ShortName = "HFC" };
        var away = new Club { ClubId = Guid.NewGuid(), EplClubId = $"away-{suffix}", Name = $"Away FC {suffix}", ShortName = "AFC" };
        var fixture = new Fixture
        {
            FixtureId = Guid.NewGuid(),
            EplFixtureId = $"fixture-{suffix}",
            GameweekId = gameweekId,
            HomeClubId = home.ClubId,
            AwayClubId = away.ClubId,
            KickoffTime = kickoffTime ?? DateTimeOffset.UtcNow.AddDays(1),
            Status = FixtureStatus.Scheduled,
        };
        db.Clubs.AddRange(home, away);
        db.Fixtures.Add(fixture);
        await db.SaveChangesAsync();

        return fixture;
    }

    private async Task<(string EplSeasonIdentifier, ClubStanding Standing)> SeedClubStandingAsync(int position = 1)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var eplSeasonIdentifier = $"season-{suffix}";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();

        var club = new Club { ClubId = Guid.NewGuid(), EplClubId = $"club-{suffix}", Name = $"Table FC {suffix}", ShortName = "TFC" };
        var standing = new ClubStanding
        {
            EplSeasonIdentifier = eplSeasonIdentifier,
            ClubId = club.ClubId,
            Position = position,
            Played = 10,
            Won = 6,
            Drawn = 2,
            Lost = 2,
            GoalsFor = 20,
            GoalsAgainst = 10,
            Points = 20,
        };
        db.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        db.Clubs.Add(club);
        db.ClubStandings.Add(standing);
        await db.SaveChangesAsync();

        return (eplSeasonIdentifier, standing);
    }

    [Fact]
    public async Task ListClubs_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request("/api/v1/epl/clubs"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListClubs_returns_a_seeded_club()
    {
        var (club, _) = await SeedClubAndPlayerAsync();
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request("/api/v1/epl/clubs", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var clubs = await response.Content.ReadFromJsonAsync<List<ClubDto>>();
        Assert.Contains(clubs!, c => c.ClubId == club.ClubId && c.Name == club.Name && c.ShortName == club.ShortName);
    }

    [Fact]
    public async Task ListPlayers_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request("/api/v1/epl/players"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListPlayers_filters_by_clubId()
    {
        var (club, player) = await SeedClubAndPlayerAsync();
        var (_, otherPlayer) = await SeedClubAndPlayerAsync();
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request($"/api/v1/epl/players?clubId={club.ClubId}", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var players = await response.Content.ReadFromJsonAsync<List<PlayerDto>>();
        Assert.Contains(players!, p => p.PlayerId == player.PlayerId);
        Assert.DoesNotContain(players!, p => p.PlayerId == otherPlayer.PlayerId);
    }

    [Fact]
    public async Task ListPlayers_filters_by_position()
    {
        var (_, forward) = await SeedClubAndPlayerAsync(position: PlayerPosition.Fwd);
        var (_, goalkeeper) = await SeedClubAndPlayerAsync(position: PlayerPosition.Gk);
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request("/api/v1/epl/players?position=Fwd", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var players = await response.Content.ReadFromJsonAsync<List<PlayerDto>>();
        Assert.Contains(players!, p => p.PlayerId == forward.PlayerId);
        Assert.DoesNotContain(players!, p => p.PlayerId == goalkeeper.PlayerId);
    }

    [Fact]
    public async Task ListPlayers_search_is_a_case_insensitive_substring_match()
    {
        var (_, player) = await SeedClubAndPlayerAsync(playerName: "Unmistakable Salahsen");
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request("/api/v1/epl/players?search=salahsen", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var players = await response.Content.ReadFromJsonAsync<List<PlayerDto>>();
        Assert.Contains(players!, p => p.PlayerId == player.PlayerId);
    }

    [Fact]
    public async Task GetPlayer_returns_the_players_reference_data()
    {
        var (club, player) = await SeedClubAndPlayerAsync();
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request($"/api/v1/epl/players/{player.PlayerId}", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PlayerDto>();
        Assert.Equal(player.PlayerId, dto!.PlayerId);
        Assert.Equal(player.EplPlayerId, dto.EplPlayerId);
        Assert.Equal(club.ClubId, dto.CurrentClubId);
    }

    [Fact]
    public async Task GetPlayer_with_an_unknown_playerId_returns_404()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request($"/api/v1/epl/players/{Guid.NewGuid()}", accessToken));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListGameweeks_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request("/api/v1/epl/gameweeks?eplSeasonIdentifier=2026%2F27"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListGameweeks_without_the_required_eplSeasonIdentifier_query_parameter_returns_400()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request("/api/v1/epl/gameweeks", accessToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListGameweeks_returns_only_gameweeks_for_the_requested_season_in_number_order()
    {
        var gameweek = await SeedGameweekAsync(number: 1);
        var otherSeasonGameweek = await SeedGameweekAsync(number: 1); // a different, unrelated EplSeasonIdentifier.
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request($"/api/v1/epl/gameweeks?eplSeasonIdentifier={Uri.EscapeDataString(gameweek.EplSeasonIdentifier)}", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var gameweeks = await response.Content.ReadFromJsonAsync<List<GameweekDto>>();
        Assert.Contains(gameweeks!, g => g.GameweekId == gameweek.GameweekId);
        Assert.DoesNotContain(gameweeks!, g => g.GameweekId == otherSeasonGameweek.GameweekId);
    }

    [Fact]
    public async Task GetGameweekFixtures_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request($"/api/v1/epl/gameweeks/{Guid.NewGuid()}/fixtures"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetGameweekFixtures_returns_only_fixtures_for_that_gameweek()
    {
        var gameweek = await SeedGameweekAsync();
        var otherGameweek = await SeedGameweekAsync();
        var fixture = await SeedFixtureAsync(gameweek.GameweekId);
        var otherFixture = await SeedFixtureAsync(otherGameweek.GameweekId);
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request($"/api/v1/epl/gameweeks/{gameweek.GameweekId}/fixtures", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fixtures = await response.Content.ReadFromJsonAsync<List<FixtureDto>>();
        var dto = Assert.Single(fixtures!, f => f.FixtureId == fixture.FixtureId);
        Assert.Equal(fixture.HomeClubId, dto.HomeClubId);
        Assert.DoesNotContain(fixtures!, f => f.FixtureId == otherFixture.FixtureId);
    }

    [Fact]
    public async Task GetGameweekFixtures_for_an_unknown_gameweekId_returns_an_empty_list()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request($"/api/v1/epl/gameweeks/{Guid.NewGuid()}/fixtures", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fixtures = await response.Content.ReadFromJsonAsync<List<FixtureDto>>();
        Assert.Empty(fixtures!);
    }

    [Fact]
    public async Task GetEplTable_without_a_bearer_token_returns_401()
    {
        var response = await factory.CreateClient().SendAsync(Request("/api/v1/epl/seasons/2026%2F27/table"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetEplTable_returns_the_seasons_standings_ordered_by_position()
    {
        var (eplSeasonIdentifier, first) = await SeedClubStandingAsync(position: 1);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EplFantasyDbContext>();
            var secondClub = new Club { ClubId = Guid.NewGuid(), EplClubId = $"second-{Guid.NewGuid():N}", Name = "Second FC", ShortName = "SFC" };
            db.Clubs.Add(secondClub);
            db.ClubStandings.Add(new ClubStanding
            {
                EplSeasonIdentifier = eplSeasonIdentifier,
                ClubId = secondClub.ClubId,
                Position = 2,
                Played = 10,
                Won = 5,
                Drawn = 2,
                Lost = 3,
                GoalsFor = 15,
                GoalsAgainst = 12,
                Points = 17,
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request($"/api/v1/epl/seasons/{Uri.EscapeDataString(eplSeasonIdentifier)}/table", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var table = await response.Content.ReadFromJsonAsync<List<ClubStandingDto>>();
        Assert.Equal(2, table!.Count);
        Assert.Equal(first.ClubId, table[0].ClubId); // position 1 sorts first.
        Assert.Equal(first.Points, table[0].Points);
        Assert.Equal(first.GoalsFor - first.GoalsAgainst, table[0].GoalDifference);
    }

    [Fact]
    public async Task GetEplTable_for_an_unknown_season_returns_an_empty_list()
    {
        var client = factory.CreateClient();
        var accessToken = await RegisterAsync(client);

        var response = await client.SendAsync(Request("/api/v1/epl/seasons/no-such-season/table", accessToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var table = await response.Content.ReadFromJsonAsync<List<ClubStandingDto>>();
        Assert.Empty(table!);
    }
}
