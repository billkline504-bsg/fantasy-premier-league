using EplFantasy.Identity;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext, so UpdateDefaultIconAsync's write commits in a single SaveChangesAsync().</summary>
public sealed class ProfileService(EplFantasyDbContext dbContext, IClock clock) : IProfileService
{
    private static readonly Error ProfileIconNotActive = new(
        "profile_icon_not_active",
        "profileIconId does not reference an active application-controlled ProfileIcon.");

    public async Task<Result<UserProfile>> UpdateDefaultIconAsync(Guid userId, Guid profileIconId, CancellationToken cancellationToken = default)
    {
        var icon = await dbContext.ProfileIcons.SingleOrDefaultAsync(i => i.ProfileIconId == profileIconId, cancellationToken);
        if (icon is null)
        {
            return Result.Failure<UserProfile>(ProfileIconNotActive);
        }

        var profile = await dbContext.UserProfiles.SingleAsync(p => p.UserId == userId, cancellationToken);

        // Throws ProfileIconNotActiveException (400) if icon.IsActive is false — deliberately not
        // caught here; it propagates to IT-F14's GlobalExceptionHandler like every other
        // DomainException, sharing the exact same errorCode/message as the Result above.
        profile.SetDefaultIcon(icon, clock.UtcNow);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(profile);
    }
}
