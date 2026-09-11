namespace EplFantasy.Competition;

/// <summary>
/// IT-42 (F-010.1, BR-118/BR-215-BR-217): (re)computes and persists one <see cref="LeagueStanding"/>
/// row per FantasyTeam for a Season, as of a given Gameweek — a snapshot, not a live view, so a
/// later Gameweek's own snapshot never overwrites an earlier one (BR-217's point-in-time history).
/// Not wired to any automatic trigger yet — no BR/ADR/Architecture section names one (the same kind
/// of deliberately-left gap IT-38's own ScheduleGenerationService left for its own trigger); calling
/// this is left for whichever future task/operator action actually needs a fresh snapshot (e.g. a
/// background job once every scheduled match in a Gameweek has a Result, or on demand for a
/// historical replay).
/// </summary>
public interface IStandingsCalculationService
{
    Task CalculateAsync(Guid seasonId, Guid asOfGameweekId, CancellationToken cancellationToken = default);
}
