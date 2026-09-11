using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Rosters;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-32 (F-007.4): correctRoster — OpenAPI Specification v1.0's
/// `/admin/rosters/{gameweekRosterId}/correct`, the same "/admin/..." flat-route convention
/// SecurityController (IT-02) already established. RosterLeagueAdministrator resolves the League
/// via GameweekRoster.FantasyTeamId → FantasyTeam.LeagueMembershipId → LeagueMembership.LeagueId,
/// since the route names no leagueId directly — the same indirection DraftLeagueAdministrator
/// (IT-26) already established for its own {draftId}-only route.
/// GameweekRosterNotLockedException/InvalidRosterCompositionException are left to propagate —
/// IT-F14's GlobalExceptionHandler already maps any DomainException to its own ErrorCode/StatusCode.
/// </summary>
[ApiController]
[Route("api/v1/admin/rosters")]
[Authorize(Policy = AuthorizationPolicies.RosterLeagueAdministrator)]
public class AdminRosterController(
    IRosterService rosterService,
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpPost("{gameweekRosterId:guid}/correct")]
    public async Task<IActionResult> CorrectRoster(Guid gameweekRosterId, RosterCorrectionRequest request, CancellationToken cancellationToken)
    {
        var roster = await rosterService.CorrectAsync(
            gameweekRosterId, request.PlayerIds, request.CaptainPlayerId, request.Reason, currentUser.UserId!.Value, cancellationToken);

        return Ok(await GameweekRosterDtoFactory.BuildAsync(dbContext, roster, cancellationToken));
    }
}
