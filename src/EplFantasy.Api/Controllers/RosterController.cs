using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Infrastructure.Idempotency;
using EplFantasy.Rosters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-29 (F-007.1): getGameweekRoster/submitGameweekRoster — OpenAPI Specification v1.0's
/// `/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster`. getGameweekRoster's own
/// x-authorization ("owner, or an active member of its League — read-only for others") is exactly
/// the existing composed FantasyTeamOwnerOrActiveLeagueMember policy (IT-07); submitGameweekRoster's
/// and setCaptain's (IT-30, F-007.2) own ("owner only") are both the existing FantasyTeamOwner
/// policy — none of the three needed a new policy.
/// InvalidRosterCompositionException/GameweekRosterNotEditableException/
/// SeasonGoalPredictionRequiredException/RosterConcurrencyConflictException are left to propagate —
/// IT-F14's GlobalExceptionHandler already maps any DomainException to its own ErrorCode/StatusCode
/// automatically, the same as every other domain-exception-throwing action in this codebase.
/// correctRoster (IT-32, F-007.4) lives in its own AdminRosterController — a flat
/// `/admin/rosters/{gameweekRosterId}/correct` route, not nested under this controller's own
/// `/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}` shape — but shares this controller's own
/// GameweekRosterDtoFactory for the response body.
/// </summary>
[ApiController]
[Route("api/v1/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster")]
public class RosterController(IRosterService rosterService, EplFantasyDbContext dbContext) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.FantasyTeamOwnerOrActiveLeagueMember)]
    public async Task<IActionResult> GetGameweekRoster(Guid fantasyTeamId, Guid gameweekId, CancellationToken cancellationToken)
    {
        // Deliberately not AsNoTracking(): xmin is a shadow property with no CLR-backed field, so
        // its value only survives materialization on a tracked entity — an EF Core gotcha, not an
        // oversight. A single-row read tracked for the rest of this request's lifetime is cheap.
        var roster = await dbContext.GameweekRosters.Include(r => r.Players)
            .SingleOrDefaultAsync(r => r.FantasyTeamId == fantasyTeamId && r.GameweekId == gameweekId, cancellationToken);

        if (roster is null)
        {
            return NotFound();
        }

        Response.Headers.ETag = EncodeETag((uint)dbContext.Entry(roster).Property("xmin").CurrentValue!);

        return Ok(await GameweekRosterDtoFactory.BuildAsync(dbContext, roster, cancellationToken));
    }

    [HttpPut]
    [Authorize(Policy = AuthorizationPolicies.FantasyTeamOwner)]
    [Idempotent]
    public async Task<IActionResult> SubmitGameweekRoster(
        Guid fantasyTeamId,
        Guid gameweekId,
        RosterSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        uint? ifMatch = null;
        if (Request.Headers.TryGetValue("If-Match", out var ifMatchHeader) && !string.IsNullOrEmpty(ifMatchHeader))
        {
            if (!TryDecodeETag(ifMatchHeader.ToString(), out var parsed))
            {
                return BadRequest();
            }

            ifMatch = parsed;
        }

        var roster = await rosterService.SubmitAsync(
            fantasyTeamId, gameweekId, request.PlayerIds, request.CaptainPlayerId, ifMatch, cancellationToken);

        return Ok(await GameweekRosterDtoFactory.BuildAsync(dbContext, roster, cancellationToken));
    }

    [HttpPut("captain")]
    [Authorize(Policy = AuthorizationPolicies.FantasyTeamOwner)]
    public async Task<IActionResult> SetCaptain(
        Guid fantasyTeamId,
        Guid gameweekId,
        SetCaptainRequest request,
        CancellationToken cancellationToken)
    {
        var roster = await rosterService.SetCaptainAsync(fantasyTeamId, gameweekId, request.CaptainPlayerId, cancellationToken);

        return Ok(await GameweekRosterDtoFactory.BuildAsync(dbContext, roster, cancellationToken));
    }

    private static string EncodeETag(uint xmin) => $"\"{xmin}\"";

    private static bool TryDecodeETag(string headerValue, out uint xmin) =>
        uint.TryParse(headerValue.Trim('"'), out xmin);
}
