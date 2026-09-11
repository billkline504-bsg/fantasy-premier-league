namespace EplFantasy.Leagues;

/// <summary>
/// F-003.1's League-creation/update orchestration (IT-03). Declared here, in the bounded context
/// that owns the League/LeagueMembership aggregates; implemented in EplFantasy.Infrastructure (it
/// needs the real DbContext and, for <see cref="UpdateAsync"/>, the shared
/// <c>IAdministrativeActionRecorder</c> — a cross-context dependency this interface itself must
/// stay free of, matching every other application-service seam in this codebase).
/// </summary>
public interface ILeagueService
{
    /// <summary>
    /// Creates the League and its founding Administrator LeagueMembership (BR-024) together, in
    /// one transaction — the mutually-referential FK the two rows share is only validated at
    /// commit (see LeagueConfigurations.cs's header comment).
    /// </summary>
    Task<League> CreateAsync(Guid creatorUserId, string name, string? description, CancellationToken cancellationToken = default);

    /// <summary>
    /// BR-025: applies a League Administrator's update to name/description/status. Every field is
    /// optional; a null argument leaves that field unchanged. Writes exactly one
    /// <c>administrative_actions</c> row (IT-F07, AP-005) in the same transaction as the change.
    /// </summary>
    Task<League> UpdateAsync(
        Guid leagueId,
        Guid actingUserId,
        string? name,
        string? description,
        LeagueStatus? status,
        CancellationToken cancellationToken = default);
}
