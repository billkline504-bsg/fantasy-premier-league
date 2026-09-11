using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext, so each method's write commits in a single SaveChangesAsync().</summary>
public sealed class MembershipService(EplFantasyDbContext dbContext, IClock clock) : IMembershipService
{
    private static readonly Error ProfileIconNotActive = new(
        "profile_icon_not_active",
        "profileIconId does not reference an active application-controlled ProfileIcon.");

    public async Task LeaveAsync(Guid leagueId, Guid membershipId, CancellationToken cancellationToken = default)
    {
        // The MembershipOwnerOrLeagueAdministrator authorization handler already confirmed a
        // membership row exists for (leagueId, membershipId) before this method is ever reached.
        var membership = await dbContext.LeagueMemberships.SingleAsync(
            m => m.LeagueMembershipId == membershipId && m.LeagueId == leagueId,
            cancellationToken);

        membership.Leave(clock.UtcNow);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Result<LeagueMembership>> SetLeagueIconAsync(Guid membershipId, Guid profileIconId, CancellationToken cancellationToken = default)
    {
        var icon = await dbContext.ProfileIcons.SingleOrDefaultAsync(i => i.ProfileIconId == profileIconId, cancellationToken);
        if (icon is null)
        {
            return Result.Failure<LeagueMembership>(ProfileIconNotActive);
        }

        // The MembershipOwner authorization handler already confirmed this row belongs to the
        // caller before this method is ever reached.
        var membership = await dbContext.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == membershipId, cancellationToken);

        // Throws LeagueIconNotActiveException (400) if icon.IsActive is false — deliberately not
        // caught here; it propagates to IT-F14's GlobalExceptionHandler like every other
        // DomainException, sharing the exact same errorCode/message as the Result above.
        membership.SetLeagueIcon(icon.ProfileIconId, icon.IsActive);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(membership);
    }

    public async Task ClearLeagueIconAsync(Guid membershipId, CancellationToken cancellationToken = default)
    {
        var membership = await dbContext.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == membershipId, cancellationToken);

        membership.ClearLeagueIcon();

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
