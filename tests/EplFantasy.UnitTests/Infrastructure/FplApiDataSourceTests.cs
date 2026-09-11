using System.Net;
using System.Text;
using EplFantasy.Infrastructure;
using EplFantasy.PlayerData;
using Xunit;

namespace EplFantasy.UnitTests.Infrastructure;

/// <summary>
/// IT-17/IT-18/IT-20/IT-22 (F-004.1/F-004.2/F-004.3/F-004.5): proves FplApiDataSource's
/// translation from the real FPL API's wire shape into FplSyncData.cs's records — against canned
/// responses (trimmed captures of the real endpoints' shapes, verified live at implementation
/// time), never the actual live, unofficial FPL API (BR-289: no availability guarantee — a
/// live-network dependency has no place in this suite).
/// </summary>
public class FplApiDataSourceTests
{
    private const string BootstrapStaticJson = """
        {
          "teams": [
            { "id": 1, "name": "Arsenal", "short_name": "ARS" },
            { "id": 14, "name": "Liverpool", "short_name": "LIV" }
          ],
          "elements": [
            { "id": 1, "first_name": "David", "second_name": "Raya Martín", "element_type": 1, "team": 1 },
            { "id": 501, "first_name": "Mohamed", "second_name": "Salah", "element_type": 4, "team": 14 }
          ],
          "events": [
            { "id": 1 },
            { "id": 2 },
            { "id": 3 }
          ]
        }
        """;

    // Gameweek 1: two fixtures — 101 kicks off earlier (17:00Z) than 100 (19:00Z), so the computed
    // RosterLockDeadline must use 101's earlier time, not just the first array entry. Gameweek 2:
    // one still-in-progress fixture. Gameweek 3 is declared in "events" above but has no fixture at
    // all, so it must not appear in GetGameweeksAsync's output. Fixture 103 has a null event (never
    // assigned to any Gameweek at all) and must be skipped entirely, not crash the batch. Fixture
    // 104 has a confirmed event but a null kickoff_time — IT-22's Postponed signal — and must be
    // included (unlike 103), not skipped.
    private const string FixturesJson = """
        [
          { "id": 100, "event": 1, "team_h": 1, "team_a": 14, "kickoff_time": "2026-08-21T19:00:00Z", "started": true, "finished": true, "team_h_score": 3, "team_a_score": 0 },
          { "id": 101, "event": 1, "team_h": 14, "team_a": 1, "kickoff_time": "2026-08-21T17:00:00Z", "started": false, "finished": false, "team_h_score": null, "team_a_score": null },
          { "id": 102, "event": 2, "team_h": 1, "team_a": 14, "kickoff_time": "2026-08-28T15:00:00Z", "started": true, "finished": false, "team_h_score": null, "team_a_score": null },
          { "id": 103, "event": null, "team_h": 1, "team_a": 14, "kickoff_time": null, "started": false, "finished": false, "team_h_score": null, "team_a_score": null },
          { "id": 104, "event": 1, "team_h": 14, "team_a": 1, "kickoff_time": null, "started": false, "finished": false, "team_h_score": null, "team_a_score": null }
        ]
        """;

    private const string LiveGameweekJson = """
        {
          "elements": [
            { "id": 1, "stats": { "minutes": 90, "total_points": 6, "goals_scored": 0, "goals_conceded": 1, "own_goals": 0 } },
            { "id": 501, "stats": { "minutes": 90, "total_points": 12, "goals_scored": 2, "goals_conceded": 0, "own_goals": 0 } }
          ]
        }
        """;

    private static FplApiDataSource CreateSource(string bootstrapJson = BootstrapStaticJson, string fixturesJson = FixturesJson, string liveJson = LiveGameweekJson)
    {
        var handler = new RoutingStubHttpMessageHandler(bootstrapJson, fixturesJson, liveJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://fantasy.premierleague.com/api/") };
        return new FplApiDataSource(httpClient);
    }

    [Fact]
    public async Task GetClubsAsync_translates_teams_into_ClubSyncData()
    {
        var clubs = await CreateSource().GetClubsAsync();

        Assert.Equal(2, clubs.Count);
        Assert.Contains(clubs, c => c is { EplClubId: "1", Name: "Arsenal", ShortName: "ARS" });
        Assert.Contains(clubs, c => c is { EplClubId: "14", Name: "Liverpool", ShortName: "LIV" });
    }

    [Fact]
    public async Task GetPlayersAsync_translates_elements_into_PlayerSyncData_with_a_combined_name_and_mapped_position()
    {
        var players = await CreateSource().GetPlayersAsync();

        Assert.Equal(2, players.Count);
        Assert.Contains(players, p => p is { EplPlayerId: "1", Name: "David Raya Martín", Position: PlayerPosition.Gk, CurrentEplClubId: "1" });
        Assert.Contains(players, p => p is { EplPlayerId: "501", Name: "Mohamed Salah", Position: PlayerPosition.Fwd, CurrentEplClubId: "14" });
    }

    [Fact]
    public async Task GetPlayersAsync_fails_safe_on_an_unrecognized_element_type()
    {
        const string json = """{ "teams": [], "elements": [{ "id": 1, "first_name": "A", "second_name": "B", "element_type": 99, "team": 1 }], "events": [] }""";

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSource(bootstrapJson: json).GetPlayersAsync());
    }

    [Fact]
    public async Task GetGameweeksAsync_computes_the_deadline_from_the_earliest_fixture_kickoff_minus_the_default_offset()
    {
        var gameweeks = await CreateSource().GetGameweeksAsync("2026/27");

        var gameweek1 = Assert.Single(gameweeks, g => g.Number == 1);
        Assert.Equal(DateTimeOffset.Parse("2026-08-21T16:00:00Z"), gameweek1.RosterLockDeadline); // 17:00 (fixture 101, the earlier kickoff) minus 60 minutes.
        Assert.Equal("2026/27", gameweek1.EplSeasonIdentifier);
    }

    [Fact]
    public async Task GetGameweeksAsync_omits_a_declared_event_with_no_fixture_at_all()
    {
        var gameweeks = await CreateSource().GetGameweeksAsync("2026/27");

        Assert.DoesNotContain(gameweeks, g => g.Number == 3);
        Assert.Equal(2, gameweeks.Count);
    }

    [Fact]
    public async Task GetFixturesAsync_translates_fixtures_and_derives_status_from_started_and_finished()
    {
        var fixtures = await CreateSource().GetFixturesAsync("2026/27");

        Assert.Equal(4, fixtures.Count); // fixture 103 (no Gameweek assignment at all) is skipped; 104 (Postponed) is not.
        Assert.Contains(fixtures, f => f is { EplFixtureId: "100", Status: FixtureStatus.Completed, HomeGoals: 3, AwayGoals: 0 });
        Assert.Contains(fixtures, f => f is { EplFixtureId: "101", Status: FixtureStatus.Scheduled });
        Assert.Contains(fixtures, f => f is { EplFixtureId: "102", Status: FixtureStatus.InProgress, HomeGoals: null, AwayGoals: null });
    }

    [Fact]
    public async Task GetFixturesAsync_maps_a_confirmed_Gameweek_with_no_confirmed_kickoff_time_to_Postponed()
    {
        var fixtures = await CreateSource().GetFixturesAsync("2026/27");

        var postponed = Assert.Single(fixtures, f => f.EplFixtureId == "104");
        Assert.Equal(FixtureStatus.Postponed, postponed.Status);
        Assert.Equal(1, postponed.GameweekNumber); // still assigned to its current Gameweek (BR-103) until officially rescheduled.
        Assert.Null(postponed.KickoffTime);
    }

    [Fact]
    public async Task GetFixturesAsync_still_skips_a_fixture_with_no_Gameweek_assignment_at_all()
    {
        var fixtures = await CreateSource().GetFixturesAsync("2026/27");

        Assert.DoesNotContain(fixtures, f => f.EplFixtureId == "103");
    }

    [Fact]
    public async Task GetPlayerPerformancesAsync_translates_live_elements_using_FPLs_own_total_points_directly()
    {
        var performances = await CreateSource().GetPlayerPerformancesAsync(1);

        Assert.Equal(2, performances.Count);
        Assert.Contains(performances, p => p is { EplPlayerId: "1", MinutesPlayed: 90, FantasyPoints: 6, Goals: 0, GoalsConceded: 1, OwnGoals: 0 });
        Assert.Contains(performances, p => p is { EplPlayerId: "501", MinutesPlayed: 90, FantasyPoints: 12, Goals: 2, GoalsConceded: 0, OwnGoals: 0 });
    }

    private sealed class RoutingStubHttpMessageHandler(string bootstrapJson, string fixturesJson, string liveJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.Contains("/live/") ? liveJson : path.Contains("fixtures") ? fixturesJson : bootstrapJson;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
