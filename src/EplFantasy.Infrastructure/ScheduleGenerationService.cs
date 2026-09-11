using EplFantasy.Competition;
using EplFantasy.Drafts;
using EplFantasy.FantasyTeams;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>
/// IT-38 (F-009.1): reuses <see cref="IDraftOrderRandomizer"/> (IT-23) for BR-108's own randomness
/// requirement — its "shuffle a FantasyTeamId list" contract is exactly what's needed here too, no
/// less generic than its Draft-order origin suggests, so a second, independently-reinvented
/// shuffle would just be needless duplication.
/// </summary>
public sealed class ScheduleGenerationService(
    EplFantasyDbContext dbContext,
    IDraftOrderRandomizer randomizer) : IScheduleGenerationService
{
    public async Task GenerateAsync(Guid seasonId, CancellationToken cancellationToken = default)
    {
        // BR-110: a schedule, once generated, persists unchanged except via an explicit
        // administrative action — calling this again is a safe no-op, not a silent regeneration.
        if (await dbContext.HeadToHeadMatches.AnyAsync(m => m.SeasonId == seasonId, cancellationToken))
        {
            return;
        }

        var season = await dbContext.Seasons.SingleAsync(s => s.SeasonId == seasonId, cancellationToken);

        var gameweekIds = await dbContext.Gameweeks
            .Where(g => g.EplSeasonIdentifier == season.EplSeasonIdentifier)
            .OrderBy(g => g.Number)
            .Select(g => g.GameweekId)
            .ToListAsync(cancellationToken);

        var fantasyTeamIds = await dbContext.FantasyTeams
            .Where(t => t.SeasonId == seasonId && t.Status == FantasyTeamStatus.Active)
            .Select(t => t.FantasyTeamId)
            .ToListAsync(cancellationToken);

        if (gameweekIds.Count == 0 || fantasyTeamIds.Count < 2)
        {
            return;
        }

        var randomizedOrder = randomizer.Shuffle(fantasyTeamIds);
        var rounds = RoundRobinScheduler.GenerateRounds(randomizedOrder, gameweekIds.Count);

        for (var i = 0; i < gameweekIds.Count; i++)
        {
            foreach (var (home, away) in rounds[i])
            {
                dbContext.HeadToHeadMatches.Add(HeadToHeadMatch.Schedule(Guid.NewGuid(), seasonId, gameweekIds[i], home, away));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
