using EplFantasy.Api.Contracts;
using EplFantasy.Drafts;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-49 (F-011.2, BR-065/BR-067/BR-068/BR-260): declareSeasonEndingInjury — OpenAPI Specification
/// v1.0's own League-Administrator-only eligibility-determination action. Unlike IT-21's automatic
/// EPL-exit path (no Administrator step at all, BR-308), this judgment can never be automated.
/// </summary>
[ApiController]
[Route("api/v1/leagues/{leagueId}/seasons/{seasonId}/squad-players/{squadPlayerId}")]
public class SquadPlayerController(IDraftService draftService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpPost("declare-season-ending-injury")]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> DeclareSeasonEndingInjury(
        Guid leagueId, Guid seasonId, Guid squadPlayerId, DeclareSeasonEndingInjuryRequest request, CancellationToken cancellationToken)
    {
        var opportunity = await draftService.DeclareSeasonEndingInjuryAsync(leagueId, squadPlayerId, request.Reason, currentUser.UserId!.Value, cancellationToken);

        // BR-287: the OpenAPI schema documents this response as always a ReplacementOpportunity,
        // but a capped League that has already reached its FantasyTeam's own cap genuinely has none
        // to return here — eligibility is still marked (see DeclareSeasonEndingInjuryAsync's own
        // remarks), so 200 with a null body is the least surprising way to represent that, rather
        // than inventing an undocumented status code for what the spec itself never anticipated.
        return Ok(opportunity is null ? null : ReplacementOpportunityDto.From(opportunity));
    }
}
