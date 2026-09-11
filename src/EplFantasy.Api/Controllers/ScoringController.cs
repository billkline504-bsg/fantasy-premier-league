using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-33 (F-008.1): getGameweekScore — OpenAPI Specification v1.0's
/// `/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score`. A 404 means the roster hasn't
/// been scored yet — still `Locked` (or no roster/score exists at all) rather than `Scored` — the
/// exact condition IGameweekScoreCalculationService's own MarkScored call is what eventually flips.
/// </summary>
[ApiController]
[Route("api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score")]
public class ScoringController(EplFantasyDbContext dbContext) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.FantasyTeamLeagueMember)]
    public async Task<IActionResult> GetGameweekScore(Guid fantasyTeamId, Guid gameweekId, CancellationToken cancellationToken)
    {
        var score = await dbContext.GameweekScores.AsNoTracking()
            .SingleOrDefaultAsync(s => s.FantasyTeamId == fantasyTeamId && s.GameweekId == gameweekId, cancellationToken);

        if (score is null)
        {
            return NotFound();
        }

        return Ok(GameweekScoreDto.From(score));
    }
}
