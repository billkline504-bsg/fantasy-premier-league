using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// F-006.4 (listReplacementOpportunities): "Caller must own fantasyTeamId, or be the League
/// Administrator." Both "leagueId" and "fantasyTeamId" are present directly in this route
/// (unlike FantasyTeamOwnerOrActiveLeagueMemberAuthorizationHandler's own routes, which sometimes
/// carry only one), so neither needs to be resolved via a join before checking it.
/// </summary>
public sealed class FantasyTeamOwnerOrLeagueAdministratorAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<FantasyTeamOwnerOrLeagueAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FantasyTeamOwnerOrLeagueAdministratorRequirement requirement)
    {
        var leagueId = RouteValueReader.GetRouteGuid(context, "leagueId");
        var fantasyTeamId = RouteValueReader.GetRouteGuid(context, "fantasyTeamId");
        var userId = currentUser.UserId;

        if (leagueId is null || fantasyTeamId is null || userId is null)
        {
            return;
        }

        var ownsFantasyTeam = await (
            from team in dbContext.FantasyTeams
            join membership in dbContext.LeagueMemberships on team.LeagueMembershipId equals membership.LeagueMembershipId
            where team.FantasyTeamId == fantasyTeamId.Value && membership.UserId == userId.Value
            select team.FantasyTeamId
        ).AnyAsync();

        if (ownsFantasyTeam)
        {
            context.Succeed(requirement);
            return;
        }

        var isAdministrator = await dbContext.LeagueMemberships.AnyAsync(m =>
            m.LeagueId == leagueId.Value && m.UserId == userId.Value && m.Status == MembershipStatus.Active && m.IsAdministrator);

        if (isAdministrator)
        {
            context.Succeed(requirement);
        }
    }
}
