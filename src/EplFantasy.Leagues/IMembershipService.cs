using EplFantasy.SharedKernel;

namespace EplFantasy.Leagues;

/// <summary>
/// LeagueMembership self-service orchestration: F-003.3's departure flow (IT-05) and F-002.2's
/// League-specific icon override (IT-10). Declared here, in the bounded context that owns the
/// LeagueMembership aggregate; implemented in EplFantasy.Infrastructure (it needs the real
/// DbContext), matching every other application-service seam in this codebase.
/// </summary>
public interface IMembershipService
{
    /// <summary>
    /// BR-020–BR-022: sets the membership to `Left` (idempotent if already `Left`). Throws
    /// <see cref="SoleAdministratorCannotLeaveException"/> if membershipId names the League
    /// Administrator's own membership (BR-025/BR-283).
    /// </summary>
    Task LeaveAsync(Guid leagueId, Guid membershipId, CancellationToken cancellationToken = default);

    /// <summary>
    /// BR-007–BR-009: sets a League-specific icon override for membershipId. Fails with
    /// <c>"profile_icon_not_active"</c> if profileIconId names no ProfileIcon at all — a real but
    /// inactive one instead throws <see cref="LeagueIconNotActiveException"/> (same error code;
    /// see that type's own remarks).
    /// </summary>
    Task<Result<LeagueMembership>> SetLeagueIconAsync(Guid membershipId, Guid profileIconId, CancellationToken cancellationToken = default);

    /// <summary>BR-010/BR-275: reverts membershipId to the global default icon.</summary>
    Task ClearLeagueIconAsync(Guid membershipId, CancellationToken cancellationToken = default);
}
