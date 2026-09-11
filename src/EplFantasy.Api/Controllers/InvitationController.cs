using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-04 (F-003.2): League invitation issue/list/revoke (League-Administrator-only) and
/// acceptance (OpenAPI Specification v1.0's createInvitation/listInvitations/revokeInvitation/
/// acceptInvitation). acceptInvitation lives outside the `/leagues/{leagueId}/...` route family —
/// its security boundary is entirely the unguessable invitation token (BR-027), not an
/// object-level membership check, since no membership exists yet for the accepting caller;
/// [Authorize] (no policy) still applies so the caller's UserId — required to create the
/// LeagueMembership — comes from a real session, not a request body value the OpenAPI spec
/// doesn't even define.
/// </summary>
[ApiController]
public class InvitationController(
    IInvitationService invitationService,
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IClock clock) : ControllerBase
{
    [HttpPost("api/v1/leagues/{leagueId}/invitations")]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> CreateInvitation(Guid leagueId, CreateInvitationRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<InvitationChannel>(request.Channel, ignoreCase: true, out var channel))
        {
            return BadRequest();
        }

        var result = await invitationService.CreateInvitationAsync(leagueId, request.Destination, channel, request.SeasonId, cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status400BadRequest);
        }

        return StatusCode(StatusCodes.Status201Created, ToDto(result.Value, clock.UtcNow));
    }

    [HttpGet("api/v1/leagues/{leagueId}/invitations")]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> ListInvitations(Guid leagueId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var invitations = await dbContext.Invitations.AsNoTracking()
            .Where(i => i.LeagueId == leagueId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

        return Ok(invitations.Select(i => ToDto(i, now)).ToList());
    }

    [HttpDelete("api/v1/leagues/{leagueId}/invitations/{invitationId}")]
    [Authorize(Policy = AuthorizationPolicies.LeagueAdministrator)]
    public async Task<IActionResult> RevokeInvitation(Guid leagueId, Guid invitationId, CancellationToken cancellationToken)
    {
        var result = await invitationService.RevokeInvitationAsync(leagueId, invitationId, cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status404NotFound);
        }

        return NoContent();
    }

    [HttpPost("api/v1/invitations/{token}/accept")]
    [Authorize]
    public async Task<IActionResult> AcceptInvitation(string token, CancellationToken cancellationToken)
    {
        var result = await invitationService.AcceptInvitationAsync(token, currentUser.UserId!.Value, cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status410Gone);
        }

        var membership = result.Value;
        var user = await dbContext.Users.AsNoTracking().SingleAsync(u => u.UserId == membership.UserId, cancellationToken);
        var profile = await dbContext.UserProfiles.AsNoTracking().SingleAsync(p => p.UserId == membership.UserId, cancellationToken);

        return Ok(LeagueMembershipDto.From(membership, user.Username, profile.DefaultIconId));
    }

    // Mirrors AuthController.ToProblem — see that method's own remarks on why the fallback
    // errorCode has to be overwritten explicitly rather than passed to Problem() directly.
    private ObjectResult ToProblem(Error error, int statusCode)
    {
        var problem = Problem(detail: error.Message, statusCode: statusCode);
        ((ProblemDetails)problem.Value!).Extensions["errorCode"] = error.Code;
        return problem;
    }

    private static InvitationDto ToDto(Invitation invitation, DateTimeOffset now) => new()
    {
        InvitationId = invitation.InvitationId,
        LeagueId = invitation.LeagueId,
        SeasonId = invitation.SeasonId,
        Destination = invitation.Destination,
        Status = invitation.EffectiveStatus(now).ToString(),
        CreatedAt = invitation.CreatedAt,
        ExpiresAt = invitation.ExpiresAt,
    };
}
