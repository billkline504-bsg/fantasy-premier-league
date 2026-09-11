using EplFantasy.Competition;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

public sealed class StandingsTieBreakContextLoader(EplFantasyDbContext dbContext) : IStandingsTieBreakContextLoader
{
    public async Task<IStandingsTieBreakContext> LoadAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        var matches = await dbContext.HeadToHeadMatches
            .Where(m => m.SeasonId == seasonId)
            .ToListAsync(cancellationToken);

        var predictions = await dbContext.SeasonGoalPredictions
            .Where(p => p.SeasonId == seasonId)
            .ToDictionaryAsync(p => p.FantasyTeamId, cancellationToken);

        return new StandingsTieBreakContext(seasonId, predictions, matches);
    }
}
