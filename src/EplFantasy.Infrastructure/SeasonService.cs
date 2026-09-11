using EplFantasy.Leagues;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext, so CreateAsync's Season/SeasonConfiguration rows commit in a single SaveChangesAsync().</summary>
public sealed class SeasonService(EplFantasyDbContext dbContext) : ISeasonService
{
    private static readonly Error EplSeasonNotFound = new(
        "epl_season_not_found",
        "No official EPL season with that identifier is known to the platform yet.");

    public async Task<Result<Season>> CreateAsync(
        Guid leagueId,
        string eplSeasonIdentifier,
        DateOnly startDate,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.EplSeasons.AnyAsync(s => s.EplSeasonIdentifier == eplSeasonIdentifier, cancellationToken))
        {
            return Result.Failure<Season>(EplSeasonNotFound);
        }

        // BR-292: the League's *current* default configuration, snapshotted once, not a live
        // reference — see SeasonConfiguration.CopyFrom's own remarks.
        var leagueConfiguration = await dbContext.LeagueConfigurations.SingleAsync(c => c.LeagueId == leagueId, cancellationToken);

        var seasonId = Guid.NewGuid();
        var season = Season.Create(seasonId, leagueId, eplSeasonIdentifier, startDate);
        var seasonConfiguration = SeasonConfiguration.CopyFrom(seasonId, leagueConfiguration);

        dbContext.Seasons.Add(season);
        dbContext.SeasonConfigurations.Add(seasonConfiguration);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(season);
    }
}
