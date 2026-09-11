namespace EplFantasy.Scoring;

/// <summary>
/// IT-33 (F-008.1, BR-073-BR-077/BR-079/BR-288): computes and persists a <see cref="GameweekScore"/>
/// for every FantasyTeam whose Gameweek roster is <c>Locked</c> and does not already have one —
/// "triggered on PlayerPerformance sync completion, for already-locked rosters" per the
/// Implementation Task Breakdown's own Technical tasks line. A FantasyTeam that already has a
/// GameweekScore row for this Gameweek is left untouched (BR-079: a finalized result is preserved,
/// not silently recalculated) — recomputing one is a later task's concern (F-008.5/IT-37's
/// override-driven cascade), not this method's.
/// </summary>
public interface IGameweekScoreCalculationService
{
    Task CalculateForGameweekAsync(Guid gameweekId, CancellationToken cancellationToken = default);

    /// <summary>
    /// IT-37 (F-008.5 AC5, BR-148-style consistency): the "recalculation cascade" createScoreOverride/
    /// undoScoreOverride each trigger — the opposite case from <see cref="CalculateForGameweekAsync"/>,
    /// which deliberately leaves an already-<c>Scored</c> FantasyTeam alone (BR-079). This instead
    /// finds every already-<c>Scored</c> GameweekRoster for <paramref name="playerPerformanceId"/>'s
    /// own Gameweek that actually rosters that player, recomputes its GameweekScore from scratch
    /// (the override/undo is already visible to <c>IAuthoritativeValueResolver</c> by the time this
    /// runs), and updates the existing row in place — bumping <see cref="GameweekScore.RecalculatedCount"/>
    /// (BR-148) rather than inserting a duplicate. The cascade stops at GameweekScore: HeadToHeadMatch/
    /// LeagueStanding don't exist as computed aggregates yet (EPIC-009, IT-38 onward) to cascade
    /// into.
    /// </summary>
    Task RecalculateForPlayerPerformanceAsync(Guid playerPerformanceId, CancellationToken cancellationToken = default);
}
