using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// getGameweekScore's own x-authorization (BR-163): "Caller must be an active member of the
/// FantasyTeam's League" — unlike getGameweekRoster's own FantasyTeamOwnerOrActiveLeagueMember
/// policy, there is no owner shortcut here: the FantasyTeam owner still has to actually be an
/// active League member, the same as anyone else. Resolves the League via
/// FantasyTeam.LeagueMembershipId → LeagueMembership.LeagueId, since the route
/// (/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/score) names no leagueId at all.
/// </summary>
public sealed class FantasyTeamLeagueMemberAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<FantasyTeamLeagueMemberRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FantasyTeamLeagueMemberRequirement requirement)
    {
        var fantasyTeamId = RouteValueReader.GetRouteGuid(context, "fantasyTeamId");
        var userId = currentUser.UserId;

        if (fantasyTeamId is null || userId is null)
        {
            return;
        }

        var leagueId = await (
            from team in dbContext.FantasyTeams
            join membership in dbContext.LeagueMemberships on team.LeagueMembershipId equals membership.LeagueMembershipId
            where team.FantasyTeamId == fantasyTeamId.Value
            select (Guid?)membership.LeagueId
        ).SingleOrDefaultAsync();

        if (leagueId is null)
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
