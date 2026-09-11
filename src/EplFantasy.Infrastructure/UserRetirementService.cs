using EplFantasy.Identity;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext, so RetireAsync's write commits in a single SaveChangesAsync().</summary>
public sealed class UserRetirementService(EplFantasyDbContext dbContext, IClock clock) : IUserRetirementService
{
    public async Task RetireAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.SingleAsync(u => u.UserId == userId, cancellationToken);

        user.Retire(clock.UtcNow);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
