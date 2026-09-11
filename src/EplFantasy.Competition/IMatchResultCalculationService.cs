namespace EplFantasy.Competition;

/// <summary>
/// IT-39 (F-009.2, BR-111-BR-114): recalculates a single Gameweek's <see cref="HeadToHeadMatch"/>
/// result for whichever FantasyTeam just gained a fresh <c>GameweekScore</c> — called as a
/// cascade step from <c>IGameweekScoreCalculationService</c> (both the initial scoring pass and
/// any later ScoreOverride-driven recalculation), never invoked directly by a controller. A no-op
/// whenever the FantasyTeam has no scheduled match for that Gameweek (BR-306 byes) or the
/// opposing side hasn't been scored yet — F-009.2 AC4 only requires the result once *both* sides
/// are Locked and Scored, not the instant either one alone finishes.
/// </summary>
public interface IMatchResultCalculationService
{
    Task CalculateForFantasyTeamGameweekAsync(Guid fantasyTeamId, Guid gameweekId, CancellationToken cancellationToken = default);
}
