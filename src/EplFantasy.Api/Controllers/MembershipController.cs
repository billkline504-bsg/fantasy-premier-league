using EplFantasy.Api.Contracts;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Leagues;
using EplFantasy.Notifications;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Api.Controllers;

/// <summary>
/// IT-05 (F-003.3): League membership listing/reading (any active member) and leaving (self-service
/// or League-Administrator-on-behalf-of, OpenAPI Specification v1.0's listMemberships/getMembership/
/// leaveLeague). IT-10 (F-002.2) adds the League-specific icon override (setLeagueIcon/
/// clearLeagueIcon) — self-service only, no Administrator-on-behalf-of fallback. IT-53 (F-012.1)
/// adds getNotificationPreferences/updateNotificationPreferences — also self-service only, the
/// same MembershipOwner policy the icon endpoints already established for this exact route shape.
/// </summary>
[ApiController]
[Route("api/v1/leagues/{leagueId}/memberships")]
public class MembershipController(
    IMembershipService membershipService,
    INotificationPreferenceService notificationPreferenceService,
    EplFantasyDbContext dbContext) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> ListMemberships(Guid leagueId, CancellationToken cancellationToken)
    {
        var memberships = await dbContext.LeagueMemberships.AsNoTracking()
            .Where(m => m.LeagueId == leagueId)
            .ToListAsync(cancellationToken);

        var userIds = memberships.Select(m => m.UserId).ToList();
        var users = await dbContext.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, cancellationToken);
        var profiles = await dbContext.UserProfiles.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, cancellationToken);

        var dtos = memberships
            .Select(m => LeagueMembershipDto.From(m, users[m.UserId].Username, profiles[m.UserId].DefaultIconId))
            .ToList();

        return Ok(dtos);
    }

    [HttpGet("{membershipId}")]
    [Authorize(Policy = AuthorizationPolicies.ActiveLeagueMember)]
    public async Task<IActionResult> GetMembership(Guid leagueId, Guid membershipId, CancellationToken cancellationToken)
    {
        var membership = await dbContext.LeagueMemberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.LeagueMembershipId == membershipId && m.LeagueId == leagueId, cancellationToken);

        if (membership is null)
        {
            return NotFound();
        }

        var user = await dbContext.Users.AsNoTracking().SingleAsync(u => u.UserId == membership.UserId, cancellationToken);
        var profile = await dbContext.UserProfiles.AsNoTracking().SingleAsync(p => p.UserId == membership.UserId, cancellationToken);

        return Ok(LeagueMembershipDto.From(membership, user.Username, profile.DefaultIconId));
    }

    [HttpPost("{membershipId}/leave")]
    [Authorize(Policy = AuthorizationPolicies.MembershipOwnerOrLeagueAdministrator)]
    public async Task<IActionResult> LeaveLeague(Guid leagueId, Guid membershipId, CancellationToken cancellationToken)
    {
        await membershipService.LeaveAsync(leagueId, membershipId, cancellationToken);

        return NoContent();
    }

    [HttpPut("{membershipId}/icon")]
    [Authorize(Policy = AuthorizationPolicies.MembershipOwner)]
    public async Task<IActionResult> SetLeagueIcon(Guid leagueId, Guid membershipId, SetLeagueIconRequest request, CancellationToken cancellationToken)
    {
        var result = await membershipService.SetLeagueIconAsync(membershipId, request.ProfileIconId!.Value, cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Error, StatusCodes.Status400BadRequest);
        }

        var membership = result.Value;
        var user = await dbContext.Users.AsNoTracking().SingleAsync(u => u.UserId == membership.UserId, cancellationToken);
        var profile = await dbContext.UserProfiles.AsNoTracking().SingleAsync(p => p.UserId == membership.UserId, cancellationToken);

        return Ok(LeagueMembershipDto.From(membership, user.Username, profile.DefaultIconId));
    }

    [HttpDelete("{membershipId}/icon")]
    [Authorize(Policy = AuthorizationPolicies.MembershipOwner)]
    public async Task<IActionResult> ClearLeagueIcon(Guid leagueId, Guid membershipId, CancellationToken cancellationToken)
    {
        await membershipService.ClearLeagueIconAsync(membershipId, cancellationToken);

        return NoContent();
    }

    [HttpGet("{membershipId}/notification-preferences")]
    [Authorize(Policy = AuthorizationPolicies.MembershipOwner)]
    public async Task<IActionResult> GetNotificationPreferences(Guid leagueId, Guid membershipId, CancellationToken cancellationToken)
    {
        var preferences = await notificationPreferenceService.GetPreferencesAsync(membershipId, cancellationToken);

        return Ok(preferences.Select(NotificationPreferenceDto.From).ToList());
    }

    [HttpPut("{membershipId}/notification-preferences")]
    [Authorize(Policy = AuthorizationPolicies.MembershipOwner)]
    public async Task<IActionResult> UpdateNotificationPreferences(
        Guid leagueId, Guid membershipId, List<NotificationPreferenceDto> request, CancellationToken cancellationToken)
    {
        var changes = new List<NotificationPreferenceChange>();
        foreach (var item in request)
        {
            if (!Enum.TryParse<NotificationEventType>(item.EventType, ignoreCase: true, out var eventType)
                || !Enum.TryParse<NotificationChannel>(item.Channel, ignoreCase: true, out var channel))
            {
                return BadRequest();
            }

            changes.Add(new NotificationPreferenceChange(eventType, channel, item.Enabled));
        }

        var preferences = await notificationPreferenceService.UpdatePreferencesAsync(membershipId, changes, cancellationToken);

        return Ok(preferences.Select(NotificationPreferenceDto.From).ToList());
    }

    // Mirrors AuthController.ToProblem — see that method's own remarks on why the fallback
    // errorCode has to be overwritten explicitly rather than passed to Problem() directly.
    private ObjectResult ToProblem(Error error, int statusCode)
    {
        var problem = Problem(detail: error.Message, statusCode: statusCode);
        ((ProblemDetails)problem.Value!).Extensions["errorCode"] = error.Code;
        return problem;
    }
}
