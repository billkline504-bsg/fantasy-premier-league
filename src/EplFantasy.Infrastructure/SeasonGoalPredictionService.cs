using EplFantasy.Competition;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>Registered Scoped (see ServiceCollectionExtensions.AddInfrastructure) — the same lifetime as EplFantasyDbContext, so SubmitAsync's write commits in a single SaveChangesAsync().</summary>
public sealed class SeasonGoalPredictionService(EplFantasyDbContext dbContext, IClock clock) : ISeasonGoalPredictionService
{
    private static readonly Error FantasyTeamNotFound = new(
        "fantasy_team_not_found",
        "No FantasyTeam with that id exists in this Season.");

    public async Task<Result<SeasonGoalPrediction>> SubmitAsync(
        Guid seasonId,
        Guid fantasyTeamId,
        int predictedEplGoals,
        CancellationToken cancellationToken = default)
    {
        var season = await dbContext.Seasons.SingleOrDefaultAsync(s => s.SeasonId == seasonId, cancellationToken);
        var fantasyTeamBelongsToSeason = season is not null
            && await dbContext.FantasyTeams.AnyAsync(t => t.FantasyTeamId == fantasyTeamId && t.SeasonId == seasonId, cancellationToken);

        if (season is null || !fantasyTeamBelongsToSeason)
        {
            return Result.Failure<SeasonGoalPrediction>(FantasyTeamNotFound);
        }

        var now = clock.UtcNow;
        var lockedAt = ComputeLockedAt(season.StartDate, now);

        var existing = await dbContext.SeasonGoalPredictions.SingleOrDefaultAsync(
            p => p.SeasonId == seasonId && p.FantasyTeamId == fantasyTeamId,
            cancellationToken);

        if (existing is not null)
        {
            // Throws SeasonGoalPredictionLockedException (409) if already locked — deliberately
            // not caught here; it propagates to IT-F14's GlobalExceptionHandler like every other
            // DomainException.
            existing.UpdatePrediction(predictedEplGoals, now, lockedAt);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result.Success(existing);
        }

        var prediction = SeasonGoalPrediction.Submit(Guid.NewGuid(), seasonId, fantasyTeamId, predictedEplGoals, now, lockedAt);
        dbContext.SeasonGoalPredictions.Add(prediction);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(prediction);
    }

    // BR-127/BR-128: locks at Season start under normal submission. BR-299: if the Season has
    // already started (this submission is happening late), it locks immediately instead.
    private static DateTimeOffset ComputeLockedAt(DateOnly seasonStartDate, DateTimeOffset now)
    {
        var seasonStart = new DateTimeOffset(seasonStartDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return now >= seasonStart ? now : seasonStart;
    }
}
