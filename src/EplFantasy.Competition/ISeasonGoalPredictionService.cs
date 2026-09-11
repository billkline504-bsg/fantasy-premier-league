using EplFantasy.SharedKernel;

namespace EplFantasy.Competition;

/// <summary>
/// F-010.3's Season Goal Prediction submission orchestration (IT-07 — submission only; tie-break
/// *usage* of an already-submitted prediction is IStandingsTieBreakRule's concern, Milestone 3).
/// Declared here, in the bounded context that owns the SeasonGoalPrediction aggregate; implemented
/// in EplFantasy.Infrastructure (it needs the real DbContext), matching every other
/// application-service seam in this codebase.
/// </summary>
public interface ISeasonGoalPredictionService
{
    /// <summary>
    /// Submits a FantasyTeam's first prediction, or updates its existing one if not yet locked
    /// (BR-128/BR-129/BR-299) — computing whichever <c>LockedAt</c> applies from the Season's own
    /// StartDate. Fails with <c>"fantasy_team_not_found"</c> if fantasyTeamId does not name a real
    /// FantasyTeam of seasonId. Throws <see cref="SeasonGoalPredictionLockedException"/> (409) if
    /// an existing prediction is already locked.
    /// </summary>
    Task<Result<SeasonGoalPrediction>> SubmitAsync(
        Guid seasonId,
        Guid fantasyTeamId,
        int predictedEplGoals,
        CancellationToken cancellationToken = default);
}
