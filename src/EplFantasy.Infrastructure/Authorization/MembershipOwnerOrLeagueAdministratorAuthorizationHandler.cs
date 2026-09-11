using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>F-003.3 (leaveLeague): "Caller must own membershipId, or be the League Administrator removing another member" — the one composed case FantasyTeamOwnerAuthorizationHandler's own remarks anticipated a future endpoint would need.</summary>
public sealed class MembershipOwnerOrLeagueAdministratorAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<MembershipOwnerOrLeagueAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MembershipOwnerOrLeagueAdministratorRequirement requirement)
    {
        var leagueId = RouteValueReader.GetRouteGuid(context, "leagueId");
        var membershipId = RouteValueReader.GetRouteGuid(context, "membershipId");
        var userId = currentUser.UserId;

        if (leagueId is null || membershipId is null || userId is null)
        {
            return;
        }

        var targetMembership = await dbContext.LeagueMemberships.SingleOrDefaultAsync(
            m => m.LeagueMembershipId == membershipId.Value && m.LeagueId == leagueId.Value);

        if (targetMembership is null)
        {
            return;
        }

        if (targetMembership.UserId == userId.Value)
        {
            context.Succeed(requirement);
            return;
        }

        var isAdministrator = await dbContext.LeagueMemberships.AnyAsync(m =>
            m.LeagueId == leagueId.Value
            && m.UserId == userId.Value
            && m.Status == MembershipStatus.Active
            && m.IsAdministrator);

        if (isAdministrator)
        {
            context.Succeed(requirement);
        }
    }
}
