using EplFantasy.Drafts;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// makeDraftPick's own x-authorization (BR-163): "Caller must own the FantasyTeam whose turn it
/// currently is." The route names only {draftId}, no fantasyTeamId — this loads the Draft itself,
/// computes whose turn it is via Draft.FantasyTeamIdForPick (the same snake-order math the pick
/// itself will be validated against), then checks that FantasyTeam's owning LeagueMembership
/// belongs to the caller, the same join FantasyTeamOwnerAuthorizationHandler uses.
/// </summary>
public sealed class DraftTurnOwnerAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<DraftTurnOwnerRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DraftTurnOwnerRequirement requirement)
    {
        var draftId = RouteValueReader.GetRouteGuid(context, "draftId");
        var userId = currentUser.UserId;

        if (draftId is null || userId is null)
        {
            return;
        }

        var draft = await dbContext.Drafts.AsNoTracking().SingleOrDefaultAsync(d => d.DraftId == draftId.Value);

        if (draft is not { Status: DraftStatus.InProgress } || draft.DraftOrder.Count == 0)
        {
            return;
        }

        var currentTurnFantasyTeamId = draft.FantasyTeamIdForPick(draft.CurrentRound, draft.CurrentPickIndex);

        var isCurrentTurnOwner = await (
            from team in dbContext.FantasyTeams
            join membership in dbContext.LeagueMemberships on team.LeagueMembershipId equals membership.LeagueMembershipId
            where team.FantasyTeamId == currentTurnFantasyTeamId && membership.UserId == userId.Value
            select team.FantasyTeamId
        ).AnyAsync();

        if (isCurrentTurnOwner)
        {
            context.Succeed(requirement);
        }
    }
}
