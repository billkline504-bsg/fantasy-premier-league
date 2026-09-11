using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EplFantasy.Leagues;
using EplFantasy.PlayerData;

namespace EplFantasy.Infrastructure;

/// <summary>
/// IT-17/IT-18/IT-20/IT-22 (F-004.1/F-004.2/F-004.3/F-004.5): the concrete
/// <see cref="IFplDataSource"/> ADR-009/IFplDataSource.cs's own remarks anticipated — calls the
/// real, unofficial FPL API (BR-289: publicly accessible but undocumented, no published schema or
/// rate-limit guarantee) and translates its wire shape into FplSyncData.cs's already-defined
/// records before anything else in the system ever sees it (AP-007). The private *Response records
/// below deliberately carry only the fields this translation actually needs, never the full
/// external shape; an unrecognized element_type fails the whole call rather than guessing (BR-289's
/// "fail safe, not fail open"), and a fixture with no confirmed Gameweek assignment at all yet is
/// skipped rather than crashing the batch, for the same reason — but a fixture with a confirmed
/// Gameweek and no confirmed kickoff_time is Postponed (IT-22), not skipped.
/// </summary>
public sealed class FplApiDataSource(HttpClient httpClient) : IFplDataSource
{
    public async Task<IReadOnlyList<ClubSyncData>> GetClubsAsync(CancellationToken cancellationToken = default)
    {
        var bootstrap = await GetBootstrapAsync(cancellationToken);

        return bootstrap.Teams
            .Select(t => new ClubSyncData(t.Id.ToString(), t.Name, t.ShortName))
            .ToArray();
    }

    public async Task<IReadOnlyList<PlayerSyncData>> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        var bootstrap = await GetBootstrapAsync(cancellationToken);

        return bootstrap.Elements
            .Select(e => new PlayerSyncData(e.Id.ToString(), $"{e.FirstName} {e.SecondName}", MapPosition(e.ElementType), e.Team.ToString()))
            .ToArray();
    }

    /// <summary>
    /// IT-18 (F-004.2, BR-092/BR-093): the FPL API's own per-event `deadline_time` is FPL's
    /// deadline, not ours (their offset before kickoff doesn't match BR-093's) — so this computes
    /// our own from the confirmed fixtures instead: first (earliest) kickoff_time among that
    /// Gameweek's fixtures, minus the BR-291 application-default `GameweekRosterLockOffsetBeforeKickoffMinutes`
    /// (60 minutes; read off a bare `new LeagueConfiguration()` rather than a duplicated literal, so
    /// this stays in sync with the one place that default is actually declared). This is
    /// necessarily a platform-level default, not any individual League's own (possibly overridden)
    /// SeasonConfiguration value — a single shared Gameweek row (one per EplSeasonIdentifier) has no
    /// one "the" Season to read an override from, since many Leagues' Seasons can point at it.
    /// A Gameweek with no fixture that has a confirmed kickoff_time yet is skipped entirely (no
    /// deadline can be computed) rather than synced with a guessed value.
    /// </summary>
    public async Task<IReadOnlyList<GameweekSyncData>> GetGameweeksAsync(string eplSeasonIdentifier, CancellationToken cancellationToken = default)
    {
        var bootstrap = await GetBootstrapAsync(cancellationToken);
        var fixtures = await GetRawFixturesAsync(cancellationToken);

        var firstKickoffByEvent = fixtures
            .Where(f => f is { Event: not null, KickoffTime: not null })
            .GroupBy(f => f.Event!.Value)
            .ToDictionary(g => g.Key, g => g.Min(f => f.KickoffTime!.Value));

        var lockOffset = TimeSpan.FromMinutes(new LeagueConfiguration().GameweekRosterLockOffsetBeforeKickoffMinutes);

        return bootstrap.Events
            .Where(e => firstKickoffByEvent.ContainsKey(e.Id))
            .Select(e => new GameweekSyncData(eplSeasonIdentifier, e.Id, firstKickoffByEvent[e.Id] - lockOffset))
            .ToArray();
    }

    /// <summary>
    /// IT-22 (F-004.5, BR-100/BR-103): a fixture with a confirmed Gameweek (Event) but no
    /// confirmed kickoff_time is FPL's own signal that it's currently Postponed — included here
    /// (not skipped, unlike a fixture with no Event assignment at all) so it stays visible/tracked
    /// through the postponement rather than silently disappearing; PlayerDataSyncService.cs's own
    /// upsert preserves its last-known KickoffTime since the database column is NOT NULL.
    /// </summary>
    public async Task<IReadOnlyList<FixtureSyncData>> GetFixturesAsync(string eplSeasonIdentifier, CancellationToken cancellationToken = default)
    {
        var fixtures = await GetRawFixturesAsync(cancellationToken);

        return fixtures
            .Where(f => f.Event is not null)
            .Select(f => new FixtureSyncData(
                f.Id.ToString(),
                eplSeasonIdentifier,
                f.Event!.Value,
                f.TeamH.ToString(),
                f.TeamA.ToString(),
                f.KickoffTime,
                MapStatus(f),
                f.TeamHScore,
                f.TeamAScore))
            .ToArray();
    }

    /// <summary>
    /// IT-20 (F-004.3, BR-075/BR-076/BR-231): one call per Gameweek returns every Player's raw
    /// stats at once — including FPL's own official `total_points` (BR-075's "use official FPL
    /// fantasy points directly," taken as-is, never recomputed here) — rather than one call per
    /// Player, matching BR-289's "defensive polling... not aggressive/high-frequency requests" far
    /// better than the alternative per-player history endpoint would.
    /// </summary>
    public async Task<IReadOnlyList<PlayerPerformanceSyncData>> GetPlayerPerformancesAsync(int gameweekNumber, CancellationToken cancellationToken = default)
    {
        var live = await httpClient.GetFromJsonAsync<LiveGameweekResponse>($"event/{gameweekNumber}/live/", cancellationToken);

        if (live is null)
        {
            throw new InvalidOperationException($"The FPL API's event/{gameweekNumber}/live endpoint returned an empty response.");
        }

        return live.Elements
            .Select(e => new PlayerPerformanceSyncData(e.Id.ToString(), e.Stats.Minutes, e.Stats.TotalPoints, e.Stats.GoalsScored, e.Stats.GoalsConceded, e.Stats.OwnGoals))
            .ToArray();
    }

    private async Task<BootstrapStaticResponse> GetBootstrapAsync(CancellationToken cancellationToken)
    {
        var bootstrap = await httpClient.GetFromJsonAsync<BootstrapStaticResponse>("bootstrap-static/", cancellationToken);

        return bootstrap ?? throw new InvalidOperationException("The FPL API's bootstrap-static endpoint returned an empty response.");
    }

    private async Task<IReadOnlyList<FixtureResponse>> GetRawFixturesAsync(CancellationToken cancellationToken)
    {
        var fixtures = await httpClient.GetFromJsonAsync<IReadOnlyList<FixtureResponse>>("fixtures/", cancellationToken);

        return fixtures ?? throw new InvalidOperationException("The FPL API's fixtures endpoint returned an empty response.");
    }

    /// <summary>FPL's element_type: 1=Goalkeeper, 2=Defender, 3=Midfielder, 4=Forward — stable for as long as classic FPL scoring has existed.</summary>
    private static PlayerPosition MapPosition(int elementType) => elementType switch
    {
        1 => PlayerPosition.Gk,
        2 => PlayerPosition.Def,
        3 => PlayerPosition.Mid,
        4 => PlayerPosition.Fwd,
        _ => throw new InvalidOperationException($"Unrecognized FPL element_type {elementType} — treating this as a signal to halt the sync (BR-289) rather than guessing a position."),
    };

    /// <summary>
    /// IT-22 (F-004.5, BR-100): a null kickoff_time on a fixture that otherwise has a confirmed
    /// Gameweek is FPL's own signal for "postponed, no confirmed time yet" — checked first, since a
    /// postponed fixture's `started`/`finished` are always false anyway. Abandoned is deliberately
    /// never derived here: the FPL fixtures endpoint exposes no distinct signal for it (no field
    /// separates "abandoned, awaiting replay" from "not finished yet"), and F-004.5's own technical
    /// tasks say this handling must be "driven entirely by what the official data source reports
    /// rather than independent application logic" — inventing a heuristic here would be exactly
    /// that. BR-104-106's abandoned-fixture scoring treatment needs no special case either way:
    /// whatever FPL's `finished`/stats eventually report for the (possibly replayed) fixture is
    /// already taken as-is, the same "official data is authoritative" principle IT-20 established.
    /// </summary>
    private static FixtureStatus MapStatus(FixtureResponse fixture) => fixture switch
    {
        { KickoffTime: null } => FixtureStatus.Postponed,
        { Finished: true } => FixtureStatus.Completed,
        { Started: true } => FixtureStatus.InProgress,
        _ => FixtureStatus.Scheduled,
    };

    private sealed record BootstrapStaticResponse(
        [property: JsonPropertyName("teams")] IReadOnlyList<TeamResponse> Teams,
        [property: JsonPropertyName("elements")] IReadOnlyList<ElementResponse> Elements,
        [property: JsonPropertyName("events")] IReadOnlyList<EventResponse> Events);

    private sealed record TeamResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("short_name")] string ShortName);

    private sealed record ElementResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("first_name")] string FirstName,
        [property: JsonPropertyName("second_name")] string SecondName,
        [property: JsonPropertyName("element_type")] int ElementType,
        [property: JsonPropertyName("team")] int Team);

    /// <summary>One per official FPL Gameweek (Id = Gameweek number); only the identifier is needed — the deadline is computed from fixtures, not FPL's own deadline_time (see GetGameweeksAsync's remarks).</summary>
    private sealed record EventResponse([property: JsonPropertyName("id")] int Id);

    private sealed record FixtureResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("event")] int? Event,
        [property: JsonPropertyName("team_h")] int TeamH,
        [property: JsonPropertyName("team_a")] int TeamA,
        [property: JsonPropertyName("kickoff_time")] DateTimeOffset? KickoffTime,
        [property: JsonPropertyName("started")] bool Started,
        [property: JsonPropertyName("finished")] bool Finished,
        [property: JsonPropertyName("team_h_score")] int? TeamHScore,
        [property: JsonPropertyName("team_a_score")] int? TeamAScore);

    private sealed record LiveGameweekResponse([property: JsonPropertyName("elements")] IReadOnlyList<LiveElementResponse> Elements);

    private sealed record LiveElementResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("stats")] LiveElementStatsResponse Stats);

    private sealed record LiveElementStatsResponse(
        [property: JsonPropertyName("minutes")] int Minutes,
        [property: JsonPropertyName("total_points")] int TotalPoints,
        [property: JsonPropertyName("goals_scored")] int GoalsScored,
        [property: JsonPropertyName("goals_conceded")] int GoalsConceded,
        [property: JsonPropertyName("own_goals")] int OwnGoals);
}
