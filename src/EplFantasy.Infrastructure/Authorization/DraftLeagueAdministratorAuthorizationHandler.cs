using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// extendDraftTimer's own x-authorization (BR-162): "Caller must be the League Administrator of
/// the Draft's League." The route names only {draftId}, no leagueId — this resolves the League via
/// Draft.SeasonId → Season.LeagueId first, then checks the same active-administrator-membership
/// fact LeagueAdministratorAuthorizationHandler already checks for routes that name leagueId directly.
/// </summary>
public sealed class DraftLeagueAdministratorAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<DraftLeagueAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DraftLeagueAdministratorRequirement requirement)
    {
        var draftId = RouteValueReader.GetRouteGuid(context, "draftId");
        var userId = currentUser.UserId;

        if (draftId is null || userId is null)
        {
            return;
        }

        var leagueId = await (
            from draft in dbContext.Drafts
            join season in dbContext.Seasons on draft.SeasonId equals season.SeasonId
            where draft.DraftId == draftId.Value
            select (Guid?)season.LeagueId
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
