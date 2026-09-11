using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure.Authorization;

/// <summary>
/// ADR-007: platform-level, never League-scoped, never exposed through any League-facing screen
/// or self-service flow, and never inferred from a cached claim — always checked against the
/// current User row, since IsSystemAdministrator is provisioned out-of-band.
/// </summary>
public sealed class SystemAdministratorAuthorizationHandler(
    EplFantasyDbContext dbContext,
    ICurrentUserAccessor currentUser) : AuthorizationHandler<SystemAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SystemAdministratorRequirement requirement)
    {
        var userId = currentUser.UserId;
        if (userId is null)
        {
            return;
        }

        var isSystemAdministrator = await dbContext.Users.AnyAsync(u =>
            u.UserId == userId.Value && u.IsSystemAdministrator);

        if (isSystemAdministrator)
        {
            context.Succeed(requirement);
        }
    }
}
