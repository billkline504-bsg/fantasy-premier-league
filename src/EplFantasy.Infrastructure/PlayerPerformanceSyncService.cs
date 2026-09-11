using EplFantasy.PlayerData;
using EplFantasy.Scoring;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EplFantasy.Infrastructure;

/// <summary>
/// IT-20 (F-004.3): the largest Phase-1 sync task — not just first-time ingestion but
/// reconciliation of corrected historical data (BR-234). Depends on IFplDataSource (PlayerData's
/// anti-corruption boundary) for the already-translated external data, exactly like
/// PlayerDataSyncService does — PlayerPerformance itself is a Scoring-context aggregate, so this
/// class lives here rather than extending PlayerDataSyncService/IPlayerDataSyncService.
/// IT-33 (F-008.1) added the call to IGameweekScoreCalculationService at the end of a sync — the
/// Implementation Task Breakdown's own words for that task are "triggered on PlayerPerformance sync
/// completion... for already-locked rosters."
/// </summary>
public sealed class PlayerPerformanceSyncService(
    EplFantasyDbContext dbContext,
    IFplDataSource dataSource,
    IClock clock,
    IGameweekScoreCalculationService gameweekScoreCalculationService,
    ILogger<PlayerPerformanceSyncService> logger) : IPlayerPerformanceSyncService
{
    public async Task SyncGameweekAsync(string eplSeasonIdentifier, int gameweekNumber, CancellationToken cancellationToken = default)
    {
        var gameweek = await dbContext.Gameweeks.SingleOrDefaultAsync(
            g => g.EplSeasonIdentifier == eplSeasonIdentifier && g.Number == gameweekNumber,
            cancellationToken);

        if (gameweek is null)
        {
            throw new InvalidOperationException(
                $"No Gameweek {gameweekNumber} exists yet for EPL season \"{eplSeasonIdentifier}\" — sync gameweeks (F-004.2) before player statistics.");
        }

        var externalPerformances = await dataSource.GetPlayerPerformancesAsync(gameweekNumber, cancellationToken);
        var playerIdsByExternalId = await dbContext.Players.ToDictionaryAsync(p => p.EplPlayerId, p => p.PlayerId, cancellationToken);
        var existingByPlayerId = await dbContext.PlayerPerformances
            .Where(pp => pp.GameweekId == gameweek.GameweekId)
            .ToDictionaryAsync(pp => pp.PlayerId, cancellationToken);

        var now = clock.UtcNow;

        foreach (var external in externalPerformances)
        {
            if (!playerIdsByExternalId.TryGetValue(external.EplPlayerId, out var playerId))
            {
                // A malformed/out-of-order batch — fail the whole sync (BR-233) rather than
                // silently persisting a statistic against no known Player.
                throw new InvalidOperationException(
                    $"PlayerPerformance references unknown player \"{external.EplPlayerId}\" — sync players (F-004.1) before player statistics.");
            }

            if (existingByPlayerId.TryGetValue(playerId, out var existing))
            {
                var isUnchanged = existing.MinutesPlayed == external.MinutesPlayed
                    && existing.FantasyPoints == external.FantasyPoints
                    && existing.Goals == external.Goals
                    && existing.GoalsConceded == external.GoalsConceded
                    && existing.OwnGoals == external.OwnGoals;

                if (isUnchanged)
                {
                    continue; // BR-234: an unchanged re-sync is a silent no-op, not a reconciliation.
                }

                var oldFantasyPoints = existing.FantasyPoints;
                existing.MinutesPlayed = external.MinutesPlayed;
                existing.FantasyPoints = external.FantasyPoints;
                existing.Goals = external.Goals;
                existing.GoalsConceded = external.GoalsConceded;
                existing.OwnGoals = external.OwnGoals;
                existing.RetrievedAt = now;

                logger.LogInformation(
                    "{Event}: PlayerPerformance for Player {PlayerId} / Gameweek {GameweekId} corrected by upstream FPL data (FantasyPoints {OldFantasyPoints} -> {NewFantasyPoints})",
                    nameof(ScoreRecalculated),
                    playerId,
                    gameweek.GameweekId,
                    oldFantasyPoints,
                    external.FantasyPoints);
            }
            else
            {
                dbContext.PlayerPerformances.Add(new PlayerPerformance
                {
                    PlayerPerformanceId = Guid.NewGuid(),
                    GameweekId = gameweek.GameweekId,
                    PlayerId = playerId,
                    MinutesPlayed = external.MinutesPlayed,
                    FantasyPoints = external.FantasyPoints,
                    Goals = external.Goals,
                    GoalsConceded = external.GoalsConceded,
                    OwnGoals = external.OwnGoals,
                    Source = PerformanceSource.OfficialFpl,
                    IsOfficial = true,
                    RetrievedAt = now,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // IT-33 (F-008.1): "triggered on PlayerPerformance sync completion... for already-locked
        // rosters" — this sync is the only thing that ever produces new official statistics for a
        // Gameweek, so it's also the natural point to attempt scoring every Locked roster that's
        // been waiting on them.
        await gameweekScoreCalculationService.CalculateForGameweekAsync(gameweek.GameweekId, cancellationToken);
    }
}
