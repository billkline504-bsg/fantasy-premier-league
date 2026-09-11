using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// F-010.3 (getSeasonGoalPrediction), IT-29 (getGameweekRoster): "Caller must own fantasyTeamId, or
/// be an active member of its League" — the other composed case FantasyTeamOwnerAuthorizationHandler's
/// own remarks anticipated a future endpoint would need. Resolves the League via
/// FantasyTeam.LeagueMembershipId → LeagueMembership.LeagueId rather than reading a route "leagueId"
/// value directly — getSeasonGoalPrediction's own route happens to carry one
/// (/leagues/{leagueId}/seasons/{seasonId}/fantasy-teams/{fantasyTeamId}/...), but getGameweekRoster's
/// does not (/fantasy-teams/{fantasyTeamId}/gameweeks/{gameweekId}/roster) — the same
/// "route only names one id, resolve the other via join" indirection DraftLeagueAdministrator/
/// DraftLeagueMember already established for {draftId}-only routes, applied here so this one
/// handler serves both route shapes rather than forking into two nearly-identical ones.
/// </summary>
public sealed class FantasyTeamOwnerOrActiveLeagueMemberAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<FantasyTeamOwnerOrActiveLeagueMemberRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FantasyTeamOwnerOrActiveLeagueMemberRequirement requirement)
    {
        var fantasyTeamId = RouteValueReader.GetRouteGuid(context, "fantasyTeamId");
        var userId = currentUser.UserId;

        if (fantasyTeamId is null || userId is null)
        {
            return;
        }

        var owningMembership = await (
            from team in dbContext.FantasyTeams
            join membership in dbContext.LeagueMemberships on team.LeagueMembershipId equals membership.LeagueMembershipId
            where team.FantasyTeamId == fantasyTeamId.Value
            select new { membership.LeagueId, membership.UserId }
        ).SingleOrDefaultAsync();

        if (owningMembership is null)
        {
            return;
        }

        if (owningMembership.UserId == userId.Value)
        {
            context.Succeed(requirement);
            return;
        }

        var isActiveMember = await dbContext.LeagueMemberships.AnyAsync(m =>
            m.LeagueId == owningMembership.LeagueId && m.UserId == userId.Value && m.Status == MembershipStatus.Active);

        if (isActiveMember)
        {
            context.Succeed(requirement);
        }
    }
}
