using EplFantasy.Drafts;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

public sealed class SecondaryDraftSchedulingService(EplFantasyDbContext dbContext) : ISecondaryDraftSchedulingService
{
    public async Task<DateOnly?> ProposeStartDateAsync(Guid seasonId, DateOnly? transferWindowCloseDate, CancellationToken cancellationToken = default)
    {
        if (transferWindowCloseDate is null)
        {
            return null; // AC4: not yet confirmed — no premature proposal.
        }

        var season = await dbContext.Seasons.SingleAsync(s => s.SeasonId == seasonId, cancellationToken);
        var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == seasonId, cancellationToken);

        var datesWithScheduledFixtures = await dbContext.Fixtures
            .Join(dbContext.Gameweeks, f => f.GameweekId, g => g.GameweekId, (f, g) => new { f.KickoffTime, g.EplSeasonIdentifier })
            .Where(x => x.EplSeasonIdentifier == season.EplSeasonIdentifier)
            .Select(x => x.KickoffTime)
            .ToListAsync(cancellationToken);

        var fixtureDates = datesWithScheduledFixtures.Select(k => DateOnly.FromDateTime(k.UtcDateTime)).ToHashSet();

        return SecondaryDraftScheduler.ProposeStartDate(
            transferWindowCloseDate, seasonConfiguration.SecondaryDraftSchedulingOffsetDays, fixtureDates);
    }
}
