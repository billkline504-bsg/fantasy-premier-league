using EplFantasy.SharedKernel;

namespace EplFantasy.FantasyTeams;

/// <summary>
/// F-002.3's FantasyTeam-creation orchestration (IT-11). Declared here, in the bounded context
/// that owns the FantasyTeam aggregate; implemented in EplFantasy.Infrastructure (it needs the
/// real DbContext), matching every other application-service seam in this codebase.
/// </summary>
public interface IFantasyTeamService
{
    /// <summary>
    /// BR-018/BR-019: creates the caller's FantasyTeam for (leagueMembershipId, seasonId). Fails
    /// with <c>"fantasy_team_already_exists"</c> if one already exists for that exact pair
    /// (BR-193, Invariant 2) — checked first, then re-confirmed against the database's own unique
    /// index if a concurrent request raced this one (the same two-layer pattern
    /// IUserAccountService.RegisterAsync already established for username/email uniqueness).
    /// </summary>
    Task<Result<FantasyTeam>> CreateAsync(Guid leagueMembershipId, Guid seasonId, CancellationToken cancellationToken = default);
}
