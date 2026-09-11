using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>F-002.2 (setLeagueIcon/clearLeagueIcon): "Caller must own membershipId" — a plain ownership check, no Administrator-on-behalf-of fallback (only the member themselves picks their own icon, unlike leaveLeague).</summary>
public sealed class MembershipOwnerAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<MembershipOwnerRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MembershipOwnerRequirement requirement)
    {
        var leagueId = RouteValueReader.GetRouteGuid(context, "leagueId");
        var membershipId = RouteValueReader.GetRouteGuid(context, "membershipId");
        var userId = currentUser.UserId;

        if (leagueId is null || membershipId is null || userId is null)
        {
            return;
        }

        var isOwner = await dbContext.LeagueMemberships.AnyAsync(m =>
            m.LeagueMembershipId == membershipId.Value && m.LeagueId == leagueId.Value && m.UserId == userId.Value);

        if (isOwner)
        {
            context.Succeed(requirement);
        }
    }
}
