using EplFantasy.Identity;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <inheritdoc cref="IUsernameHistoryResolver"/>
public sealed class UsernameHistoryResolver(EplFantasyDbContext dbContext) : IUsernameHistoryResolver
{
    public async Task<string> ResolveAsOfAsync(Guid userId, DateTimeOffset asOf, CancellationToken cancellationToken = default) =>
        await dbContext.UsernameHistories.AsNoTracking()
            .Where(h => h.UserId == userId && h.EffectiveFrom <= asOf && (h.EffectiveTo == null || h.EffectiveTo > asOf))
            .Select(h => h.Username)
            .SingleAsync(cancellationToken);

    public async Task<Dictionary<Guid, string>> ResolveManyAsOfAsync(
        IReadOnlyList<(Guid Key, Guid UserId, DateTimeOffset AsOf)> requests, CancellationToken cancellationToken = default)
    {
        var userIds = requests.Select(r => r.UserId).Distinct().ToList();
        var histories = await dbContext.UsernameHistories.AsNoTracking()
            .Where(h => userIds.Contains(h.UserId))
            .ToListAsync(cancellationToken);

        return requests.ToDictionary(
            r => r.Key,
            r => histories.Single(h => h.UserId == r.UserId && h.EffectiveFrom <= r.AsOf && (h.EffectiveTo == null || h.EffectiveTo > r.AsOf)).Username);
    }
}
