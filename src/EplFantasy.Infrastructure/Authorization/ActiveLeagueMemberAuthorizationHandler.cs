using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>BR-163: object-level check against the real, current LeagueMembership row — never a cached claim.</summary>
public sealed class ActiveLeagueMemberAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<ActiveLeagueMemberRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveLeagueMemberRequirement requirement)
    {
        var leagueId = RouteValueReader.GetRouteGuid(context, "leagueId");
        var userId = currentUser.UserId;

        if (leagueId is null || userId is null)
        {
            return;
        }

        var isActiveMember = await dbContext.LeagueMemberships.AnyAsync(m =>
            m.LeagueId == leagueId.Value && m.UserId == userId.Value && m.Status == MembershipStatus.Active);

        if (isActiveMember)
        {
            context.Succeed(requirement);
        }
    }
}
