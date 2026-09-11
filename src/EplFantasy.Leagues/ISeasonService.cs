using EplFantasy.SharedKernel;

namespace EplFantasy.Leagues;

/// <summary>
/// F-003.4's Season-creation orchestration (IT-06). Declared here, in the bounded context that
/// owns the Season/SeasonConfiguration aggregates; implemented in EplFantasy.Infrastructure (it
/// also needs to confirm the given EPL season identifier is already known platform-wide — a
/// PlayerData-context concern this interface itself must stay free of, matching every other
/// application-service seam in this codebase).
/// </summary>
public interface ISeasonService
{
    /// <summary>
    /// Creates the Season (always `Setup`) and copies the League's *current* LeagueConfiguration
    /// into a new SeasonConfiguration (BR-292), in one transaction. Fails with
    /// <c>"epl_season_not_found"</c> if no platform-level EPL season with that identifier is known
    /// yet (nothing upserts one here — that remains the EPL/FPL data sync's own job, ADR-009).
    /// </summary>
    Task<Result<Season>> CreateAsync(
        Guid leagueId,
        string eplSeasonIdentifier,
        DateOnly startDate,
        CancellationToken cancellationToken = default);
}
