using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Leagues;
using EplFantasy.Scoring;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-37 (F-008.5): createScoreOverride/undoScoreOverride — OpenAPI Specification v1.0's
/// `/admin/score-overrides` (the same `/admin/...` flat-route convention `SecurityController`
/// (IT-02) and `AdminRosterController` (IT-32) already established).
/// <see cref="CreateScoreOverride"/> deliberately does *not* use a declarative
/// <c>[Authorize(Policy = ...)]</c> the way every other Administrator-privileged action in this
/// codebase does — its target, PlayerPerformance, is platform-level, so there is no route value to
/// resolve a League from (unlike <see cref="UndoScoreOverride"/>, where the League is unambiguous
/// once the ScoreOverride already exists). See <see cref="IScoreOverrideService"/>'s own remarks
/// for why <see cref="ScoreOverrideRequest.LeagueId"/> exists at all. `ScoreOverrideAlreadyUndoneException`
/// is left to propagate — IT-F14's GlobalExceptionHandler already maps it to its own
/// ErrorCode/StatusCode.
/// </summary>
[ApiController]
[Route("api/v1/admin/score-overrides")]
public class AdminScoreOverrideController(
    IScoreOverrideService scoreOverrideService,
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreateScoreOverride(ScoreOverrideRequest request, CancellationToken cancellationToken)
    {
        var isAdministrator = await dbContext.LeagueMemberships.AnyAsync(m =>
            m.LeagueId == request.LeagueId
            && m.UserId == currentUser.UserId!.Value
            && m.Status == MembershipStatus.Active
            && m.IsAdministrator,
            cancellationToken);

        if (!isAdministrator)
        {
            return Forbid();
        }

        var scoreOverride = await scoreOverrideService.CreateAsync(
            request.PlayerPerformanceId, request.LeagueId, request.OverrideValue, request.Reason, currentUser.UserId!.Value, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ScoreOverrideDto.From(scoreOverride));
    }

    [HttpPost("{scoreOverrideId:guid}/undo")]
    [Authorize(Policy = AuthorizationPolicies.ScoreOverrideLeagueAdministrator)]
    public async Task<IActionResult> UndoScoreOverride(Guid scoreOverrideId, CancellationToken cancellationToken)
    {
        var scoreOverride = await scoreOverrideService.UndoAsync(scoreOverrideId, currentUser.UserId!.Value, cancellationToken);

        return Ok(ScoreOverrideDto.From(scoreOverride));
    }
}
