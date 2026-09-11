using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>ADR-007: League Administrator is a property of an active LeagueMembership, never a global role.</summary>
public sealed class LeagueAdministratorAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<LeagueAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        LeagueAdministratorRequirement requirement)
    {
        var leagueId = RouteValueReader.GetRouteGuid(context, "leagueId");
        var userId = currentUser.UserId;

        if (leagueId is null || userId is null)
        {
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
