using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// "Caller must own {fantasyTeamId}" — resolved by joining FantasyTeam to its owning
/// LeagueMembership, since a FantasyTeam has no UserId column of its own (Architecture §6.3:
/// FantasyTeam belongs to a LeagueMembership, not directly to a User). Only covers the strict
/// ownership check; an endpoint whose OpenAPI spec allows read access to any active League member
/// too ("owns fantasyTeamId, or is an active member of its League") composes this with
/// <see cref="ActiveLeagueMemberRequirement"/> at the controller/policy level once that endpoint
/// is actually built — not a concern this foundational handler needs to resolve on its own.
/// </summary>
public sealed class FantasyTeamOwnerAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<FantasyTeamOwnerRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FantasyTeamOwnerRequirement requirement)
    {
        var fantasyTeamId = RouteValueReader.GetRouteGuid(context, "fantasyTeamId");
        var userId = currentUser.UserId;

        if (fantasyTeamId is null || userId is null)
        {
            return;
        }

        var isOwner = await (
            from team in dbContext.FantasyTeams
            join membership in dbContext.LeagueMemberships on team.LeagueMembershipId equals membership.LeagueMembershipId
            where team.FantasyTeamId == fantasyTeamId.Value && membership.UserId == userId.Value
            select team.FantasyTeamId
        ).AnyAsync();

        if (isOwner)
        {
            context.Succeed(requirement);
        }
    }
}
