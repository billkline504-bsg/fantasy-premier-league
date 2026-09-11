using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// getDraft/listDraftSelections/getDraftPlayerPool's own x-authorization (F-005.5): "Caller must be
/// an active member of the Draft's League." The route names only {draftId}, no leagueId — resolves
/// the League via Draft.SeasonId → Season.LeagueId first, the same indirection
/// DraftLeagueAdministratorAuthorizationHandler already established for this route shape (IT-26),
/// then checks the same active-membership fact ActiveLeagueMemberAuthorizationHandler checks for
/// routes that name leagueId directly.
/// </summary>
public sealed class DraftLeagueMemberAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<DraftLeagueMemberRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DraftLeagueMemberRequirement requirement)
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

        var isActiveMember = await dbContext.LeagueMemberships.AnyAsync(m =>
            m.LeagueId == leagueId.Value && m.UserId == userId.Value && m.Status == MembershipStatus.Active);

        if (isActiveMember)
        {
            context.Succeed(requirement);
        }
    }
}
