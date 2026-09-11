using EplFantasy.SharedKernel;

namespace EplFantasy.Leagues;

/// <summary>
/// F-003.5's configuration-update orchestration (IT-08 — core mechanics only; each parameter's
/// owning feature adds its own read path against this later, per the Backlog's own note).
/// Declared here, in the bounded context that owns the LeagueConfiguration/SeasonConfiguration
/// value objects; implemented in EplFantasy.Infrastructure (it needs the real DbContext and the
/// shared IAdministrativeActionRecorder for BR-295's audit row), matching every other
/// application-service seam in this codebase.
/// </summary>
public interface IConfigurationService
{
    /// <summary>BR-292: the League-level defaults are mutable at any time — no lock check applies. Writes exactly one `ConfigurationChanged` administrative_actions row (BR-295, IT-F07).</summary>
    Task<LeagueConfiguration> UpdateLeagueConfigurationAsync(
        Guid leagueId,
        Guid actingMembershipId,
        ConfigurationValues values,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// BR-293/BR-294: overrides this Season's configuration up to each field's own lock point.
    /// Fails with <c>"season_not_found"</c> if seasonId does not name a real Season of leagueId.
    /// Throws <see cref="SeasonConfigurationFieldsLockedException"/> (409) if the request changes
    /// any already-locked field. Writes exactly one `ConfigurationChanged` administrative_actions
    /// row (BR-295, IT-F07) on success.
    /// </summary>
    Task<Result<SeasonConfiguration>> UpdateSeasonConfigurationAsync(
        Guid leagueId,
        Guid seasonId,
        Guid actingMembershipId,
        ConfigurationValues values,
        CancellationToken cancellationToken = default);
}
