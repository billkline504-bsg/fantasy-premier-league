using EplFantasy.Api.Contracts;
using EplFantasy.Drafts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-48 (F-006.4, BR-063-BR-068/BR-260-BR-264/BR-287): listReplacementOpportunities (OpenAPI
/// Specification v1.0) plus a spend action this task's own research established has no literal
/// OpenAPI operation of its own — the spec's wording loosely suggests reusing makeDraftPick, but a
/// ReplacementOpportunity never goes through the Draft/DraftSelection aggregate at all (see
/// ReplacementOpportunity's own remarks). This route's own POST sub-action is a deliberate,
/// documented addition rather than a stretched reuse of makeDraftPick's `{draftId}`-shaped route,
/// the same kind of judgment call IT-37's own ScoreOverrideRequest.LeagueId gap required.
/// </summary>
[ApiController]
[Route("api/v1/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/replacement-opportunities")]
public class ReplacementOpportunityController(IDraftService draftService, EplFantasyDbContext dbContext) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.FantasyTeamOwnerOrLeagueAdministrator)]
    public async Task<IActionResult> ListReplacementOpportunities(Guid leagueId, Guid seasonId, Guid fantasyTeamId, CancellationToken cancellationToken)
    {
        var team = await dbContext.FantasyTeams.AsNoTracking()
            .SingleOrDefaultAsync(t => t.FantasyTeamId == fantasyTeamId && t.SeasonId == seasonId, cancellationToken);
        if (team is null)
        {
            return NotFound();
        }

        var opportunities = await dbContext.ReplacementOpportunities.AsNoTracking()
            .Where(o => o.FantasyTeamId == fantasyTeamId)
            .ToListAsync(cancellationToken);

        return Ok(opportunities.Select(ReplacementOpportunityDto.From).ToList());
    }

    [HttpPost("{replacementOpportunityId}/spend")]
    [Authorize(Policy = AuthorizationPolicies.FantasyTeamOwner)]
    public async Task<IActionResult> MakeReplacementPick(
        Guid leagueId, Guid seasonId, Guid fantasyTeamId, Guid replacementOpportunityId,
        MakeReplacementPickRequest request, CancellationToken cancellationToken)
    {
        var opportunity = await draftService.MakeReplacementPickAsync(fantasyTeamId, replacementOpportunityId, request.PlayerId!.Value, cancellationToken);

        return Ok(ReplacementOpportunityDto.From(opportunity));
    }
}
