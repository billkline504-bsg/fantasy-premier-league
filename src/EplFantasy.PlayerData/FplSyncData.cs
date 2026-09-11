namespace EplFantasy.PlayerData;

// ADR-009's anti-corruption boundary shape: whatever the real, unofficial FPL API's wire format
// looks like (BR-289 — undocumented, no published schema guarantee), a concrete IFplDataSource
// implementation is responsible for translating it into exactly these records before anything
// else in the system ever sees it. No other module — and no test outside this one — should ever
// need to know what the actual external JSON/CSV shape is (AP-007).

public sealed record ClubSyncData(string EplClubId, string Name, string ShortName);

public sealed record PlayerSyncData(string EplPlayerId, string Name, PlayerPosition Position, string? CurrentEplClubId);

public sealed record GameweekSyncData(string EplSeasonIdentifier, int Number, DateTimeOffset RosterLockDeadline);

/// <summary>
/// IT-22 (F-004.5, BR-100/BR-103): KickoffTime is nullable — null is FPL's own signal that this
/// fixture is currently Postponed with no confirmed time (Status is FixtureStatus.Postponed in
/// that case). The database's fixtures.kickoff_time column is NOT NULL, so an upsert against an
/// already-known fixture preserves its last confirmed KickoffTime rather than trying to null it
/// out; a brand-new fixture never seen before with no confirmed time yet has nothing to anchor
/// that column to and is skipped until FPL confirms one. GameweekNumber is never null — a
/// postponed fixture keeps its current Gameweek assignment until FPL officially reschedules it to
/// a different one, at which point GameweekNumber simply changes value on a later sync, the same
/// upsert-by-external-id mechanism already handles.
/// </summary>
public sealed record FixtureSyncData(
    string EplFixtureId,
    string EplSeasonIdentifier,
    int GameweekNumber,
    string HomeEplClubId,
    string AwayEplClubId,
    DateTimeOffset? KickoffTime,
    FixtureStatus Status,
    int? HomeGoals,
    int? AwayGoals);

/// <summary>
/// IT-20 (F-004.3): one official gameweek's worth of a single Player's raw statistics —
/// FantasyPoints is BR-075's official FPL fantasy points value taken directly, never recomputed by
/// this application (BR-073/BR-075/BR-078). No EplSeasonIdentifier field here (unlike
/// GameweekSyncData/FixtureSyncData) because PlayerPerformance itself has none — it's keyed by
/// GameweekId alone (V008); the real FPL API's own per-gameweek live-stats endpoint has no season
/// concept either, the same single-current-season limitation GetGameweeksAsync/GetFixturesAsync
/// already have.
/// </summary>
public sealed record PlayerPerformanceSyncData(string EplPlayerId, int MinutesPlayed, int FantasyPoints, int Goals, int GoalsConceded, int OwnGoals);
