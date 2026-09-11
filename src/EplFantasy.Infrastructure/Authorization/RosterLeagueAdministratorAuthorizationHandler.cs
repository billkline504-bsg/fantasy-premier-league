using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// correctRoster's own x-authorization (BR-162): "Caller must be the League Administrator of the
/// roster's League." The route names only {gameweekRosterId} — this resolves the League via
/// GameweekRoster.FantasyTeamId → FantasyTeam.LeagueMembershipId → LeagueMembership.LeagueId, the
/// same indirection DraftLeagueAdministratorAuthorizationHandler already established for its own
/// {draftId}-only route.
/// </summary>
public sealed class RosterLeagueAdministratorAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<RosterLeagueAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RosterLeagueAdministratorRequirement requirement)
    {
        var gameweekRosterId = RouteValueReader.GetRouteGuid(context, "gameweekRosterId");
        var userId = currentUser.UserId;

        if (gameweekRosterId is null || userId is null)
        {
            return;
        }

        var leagueId = await (
            from roster in dbContext.GameweekRosters
            join team in dbContext.FantasyTeams on roster.FantasyTeamId equals team.FantasyTeamId
            join membership in dbContext.LeagueMemberships on team.LeagueMembershipId equals membership.LeagueMembershipId
            where roster.GameweekRosterId == gameweekRosterId.Value
            select (Guid?)membership.LeagueId
        ).SingleOrDefaultAsync();

        if (leagueId is null)
        {
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
