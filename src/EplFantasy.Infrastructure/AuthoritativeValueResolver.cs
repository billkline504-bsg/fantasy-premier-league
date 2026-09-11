using EplFantasy.Scoring;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

public sealed class AuthoritativeValueResolver(EplFantasyDbContext dbContext) : IAuthoritativeValueResolver
{
    public async Task<TValue> ResolveAsync<TValue>(
        Guid playerPerformanceId,
        Func<ScoreOverride, TValue> fromActiveOverride,
        Func<PlayerPerformance, TValue> fromOfficialData,
        Func<TValue> applicationCalculation,
        CancellationToken cancellationToken = default)
    {
        // "Active" = not undone; if more than one somehow exists (undo-then-reoverride leaves the
        // prior row in place, just no longer active), the most recently created wins.
        var activeOverride = await dbContext.ScoreOverrides
            .Where(o => o.PlayerPerformanceId == playerPerformanceId && o.UndoneAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (activeOverride is not null)
        {
            return fromActiveOverride(activeOverride);
        }

        var performance = await dbContext.PlayerPerformances
            .SingleOrDefaultAsync(p => p.PlayerPerformanceId == playerPerformanceId, cancellationToken);

        if (performance is { IsOfficial: true })
        {
            return fromOfficialData(performance);
        }

        return applicationCalculation();
    }
}
