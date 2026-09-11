using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// undoScoreOverride's own x-authorization (BR-162): "Caller must be the League Administrator of
/// the override's League." The route names only {scoreOverrideId} — this resolves the League via
/// ScoreOverride.AdministratorMembershipId → LeagueMembership.LeagueId, the same route-shape
/// indirection RosterLeagueAdministrator/DraftLeagueAdministrator already established for their own
/// narrower routes. Unlike createScoreOverride (see IScoreOverrideService's own remarks on why),
/// this is unambiguous: an already-existing ScoreOverride was created under exactly one League.
/// </summary>
public sealed class ScoreOverrideLeagueAdministratorAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<ScoreOverrideLeagueAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScoreOverrideLeagueAdministratorRequirement requirement)
    {
        var scoreOverrideId = RouteValueReader.GetRouteGuid(context, "scoreOverrideId");
        var userId = currentUser.UserId;

        if (scoreOverrideId is null || userId is null)
        {
            return;
        }

        var leagueId = await (
            from scoreOverride in dbContext.ScoreOverrides
            join membership in dbContext.LeagueMemberships on scoreOverride.AdministratorMembershipId equals membership.LeagueMembershipId
            where scoreOverride.ScoreOverrideId == scoreOverrideId.Value
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
