using System.Text.Json;
using EplFantasy.Competition;
using EplFantasy.Notifications;
using EplFantasy.PlayerData;
using EplFantasy.Rosters;
using EplFantasy.Scoring;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EplFantasy.Infrastructure;

/// <summary>
/// IT-33 (F-008.1): for every <c>Locked</c> GameweekRoster of the given Gameweek that doesn't
/// already have a GameweekScore (BR-079 — a finalized result is preserved, not recalculated), reads
/// each rostered player's official statistics via <see cref="IAuthoritativeValueResolver"/> (AC1,
/// respecting Invariant 12's Active-ScoreOverride-first precedence). A player with no
/// <see cref="PlayerPerformance"/> row yet for this Gameweek (statistics haven't synced, or they
/// didn't play) contributes zero across every statistic — there is nothing to resolve a value from.
/// IT-34 (F-008.2/F-008.3) hands each player's resolved points to
/// <c>GameweekRoster.DetermineSelectionRoles</c>, which applies the Captain multiplier, ranks the
/// Starting XI, and returns each player's own final points — this service sums the Starting XI's
/// total and reads the Captain's own contribution back out. IT-36 (F-008.4) separately resolves
/// Goals/GoalsConceded/OwnGoals for the *entire* roster (BR-080's own explicit "not just Starting
/// XI" distinction) and hands them to <see cref="GameweekScore.CalculateFantasyGoals"/>. Finally
/// marks the roster <c>Scored</c>. IT-37 (F-008.5) reuses this exact same computation
/// (<see cref="ComputeScoreComponentsAsync"/>) for <see cref="RecalculateForPlayerPerformanceAsync"/>
/// — an override/undo re-derives every field from scratch the same way a first-time calculation
/// does, just updating the existing row instead of inserting a new one.
///
/// IT-56 (F-012.3, BR-153/BR-338): <see cref="CalculateForGameweekAsync"/>'s own first-time-only
/// guard (a FantasyTeam already scored for this Gameweek is skipped) makes it exactly the
/// "finalized" moment AC1 describes — so a `WeeklyScore` NotificationRequest is queued alongside
/// each newly-inserted GameweekScore, in the same SaveChanges, never from
/// <see cref="RecalculateForPlayerPerformanceAsync"/>'s override-driven cascade (that revises an
/// already-finalized result; re-notifying on every subsequent override would be exactly the noise
/// BR-338's own "without one League's notifications forcing my hand" framing warns against). As in
/// IT-55, preferences are never read here — one row per <see cref="NotificationChannel"/> is queued
/// unconditionally, and the already-built outbox dispatcher (IT-F12) is solely responsible for
/// BR-226's suppression.
/// </summary>
public sealed class GameweekScoreCalculationService(
    EplFantasyDbContext dbContext,
    IAuthoritativeValueResolver authoritativeValueResolver,
    IMatchResultCalculationService matchResultCalculationService,
    IClock clock,
    ILogger<GameweekScoreCalculationService> logger) : IGameweekScoreCalculationService
{
    private sealed record WeeklyScorePayload(Guid GameweekId, int FantasyPoints);

    public async Task CalculateForGameweekAsync(Guid gameweekId, CancellationToken cancellationToken = default)
    {
        var alreadyScoredFantasyTeamIds = await dbContext.GameweekScores
            .Where(s => s.GameweekId == gameweekId)
            .Select(s => s.FantasyTeamId)
            .ToListAsync(cancellationToken);

        var rosters = await dbContext.GameweekRosters.Include(r => r.Players)
            .Where(r => r.GameweekId == gameweekId
                && r.Status == RosterStatus.Locked
                && !alreadyScoredFantasyTeamIds.Contains(r.FantasyTeamId))
            .ToListAsync(cancellationToken);

        if (rosters.Count == 0)
        {
            return;
        }

        var now = clock.UtcNow;
        var membershipByFantasyTeamId = await ResolveMembershipsAsync(
            rosters.Select(r => r.FantasyTeamId).ToList(), cancellationToken);

        foreach (var roster in rosters)
        {
            var (fantasyPoints, captainPoints, goalsFor, goalsAgainst, goalDifference) =
                await ComputeScoreComponentsAsync(roster, gameweekId, cancellationToken);

            var gameweekScore = GameweekScore.Calculate(
                Guid.NewGuid(), roster.FantasyTeamId, gameweekId,
                fantasyPoints, captainPoints, goalsFor, goalsAgainst, goalDifference, now);
            dbContext.GameweekScores.Add(gameweekScore);

            roster.MarkScored();

            var (membershipId, userId) = membershipByFantasyTeamId[roster.FantasyTeamId];
            var payloadJson = JsonSerializer.Serialize(new WeeklyScorePayload(gameweekId, gameweekScore.FantasyPoints));
            foreach (var channel in Enum.GetValues<NotificationChannel>())
            {
                dbContext.NotificationRequests.Add(new NotificationRequest
                {
                    RequestId = Guid.NewGuid(),
                    UserId = userId,
                    LeagueMembershipId = membershipId,
                    EventType = NotificationEventType.WeeklyScore,
                    Channel = channel,
                    PayloadJson = payloadJson,
                    Status = NotificationStatus.Pending,
                    CreatedAt = now,
                });
            }

            logger.LogInformation(
                "{Event}: FantasyTeam {FantasyTeamId} scored {FantasyPoints} point(s) for Gameweek {GameweekId}",
                "GameweekScored", roster.FantasyTeamId, gameweekScore.FantasyPoints, gameweekId);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // IT-39 (F-009.2): the cascade runs as a separate step after this method's own
        // SaveChangesAsync has committed — MatchResultCalculationService queries GameweekScores
        // back out of the DB, which wouldn't yet see the rows just Add()'d above if this ran
        // beforehand (the same ordering already established for ScoreOverride's cascade, IT-37).
        foreach (var roster in rosters)
        {
            await matchResultCalculationService.CalculateForFantasyTeamGameweekAsync(roster.FantasyTeamId, gameweekId, cancellationToken);
        }
    }

    public async Task RecalculateForPlayerPerformanceAsync(Guid playerPerformanceId, CancellationToken cancellationToken = default)
    {
        var performance = await dbContext.PlayerPerformances.SingleAsync(p => p.PlayerPerformanceId == playerPerformanceId, cancellationToken);

        // Only a FantasyTeam that has already been scored has a GameweekScore row to recompute —
        // a still-Locked roster will pick up the corrected value the first time it's ever scored,
        // needing no separate recalculation path.
        var affectedRosters = await dbContext.GameweekRosters.Include(r => r.Players)
            .Where(r => r.GameweekId == performance.GameweekId
                && r.Status == RosterStatus.Scored
                && r.Players.Any(p => p.PlayerId == performance.PlayerId))
            .ToListAsync(cancellationToken);

        if (affectedRosters.Count == 0)
        {
            return;
        }

        var now = clock.UtcNow;

        foreach (var roster in affectedRosters)
        {
            var (fantasyPoints, captainPoints, goalsFor, goalsAgainst, goalDifference) =
                await ComputeScoreComponentsAsync(roster, performance.GameweekId, cancellationToken);

            var gameweekScore = await dbContext.GameweekScores.SingleAsync(
                s => s.FantasyTeamId == roster.FantasyTeamId && s.GameweekId == performance.GameweekId, cancellationToken);
            gameweekScore.Recalculate(fantasyPoints, captainPoints, goalsFor, goalsAgainst, goalDifference, now);

            logger.LogInformation(
                "{Event}: FantasyTeam {FantasyTeamId}'s GameweekScore recalculated for Gameweek {GameweekId} (RecalculatedCount now {RecalculatedCount})",
                "GameweekScoreRecalculated", roster.FantasyTeamId, performance.GameweekId, gameweekScore.RecalculatedCount);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // IT-39 (F-009.2): a recalculation can flip an already-decided match's own result (the
        // task breakdown's own required case) — the same "separate step after commit" ordering as
        // CalculateForGameweekAsync above.
        foreach (var roster in affectedRosters)
        {
            await matchResultCalculationService.CalculateForFantasyTeamGameweekAsync(roster.FantasyTeamId, performance.GameweekId, cancellationToken);
        }
    }

    private async Task<(int FantasyPoints, int CaptainPoints, int GoalsFor, int GoalsAgainst, int GoalDifference)> ComputeScoreComponentsAsync(
        GameweekRoster roster, Guid gameweekId, CancellationToken cancellationToken)
    {
        var playerIds = roster.Players.Select(p => p.PlayerId).ToList();
        var performanceByPlayerId = await dbContext.PlayerPerformances
            .Where(pp => pp.GameweekId == gameweekId && playerIds.Contains(pp.PlayerId))
            .ToDictionaryAsync(pp => pp.PlayerId, cancellationToken);
        var positionByPlayerId = await dbContext.Players
            .Where(p => playerIds.Contains(p.PlayerId))
            .ToDictionaryAsync(p => p.PlayerId, p => p.Position, cancellationToken);

        var pointsByPlayerId = new Dictionary<Guid, int>();
        var goalsByPlayerId = new Dictionary<Guid, int>();
        var goalsConcededByPlayerId = new Dictionary<Guid, int>();
        var ownGoalsByPlayerId = new Dictionary<Guid, int>();

        foreach (var playerId in playerIds)
        {
            performanceByPlayerId.TryGetValue(playerId, out var performance);
            pointsByPlayerId[playerId] = await ResolveStatAsync(performance, "fantasyPoints", p => p.FantasyPoints, cancellationToken);
            goalsByPlayerId[playerId] = await ResolveStatAsync(performance, "goals", p => p.Goals, cancellationToken);
            goalsConcededByPlayerId[playerId] = await ResolveStatAsync(performance, "goalsConceded", p => p.GoalsConceded, cancellationToken);
            ownGoalsByPlayerId[playerId] = await ResolveStatAsync(performance, "ownGoals", p => p.OwnGoals, cancellationToken);
        }

        // IT-37: re-running this on an already-Scored roster (a recalculation) re-derives
        // SelectionRole too, not just the numeric totals — an override that shifts a player's
        // points enough to change the Starting XI/Bench boundary must actually move them, not
        // leave a stale role in place (AC5's "propagates consistently").
        var finalPointsByPlayerId = roster.DetermineSelectionRoles(pointsByPlayerId);

        var fantasyPoints = roster.Players
            .Where(p => p.SelectionRole == SelectionRole.StartingXi)
            .Sum(p => finalPointsByPlayerId[p.PlayerId]);
        var captainPoints = roster.CaptainPlayerId is { } captainId ? finalPointsByPlayerId.GetValueOrDefault(captainId) : 0;

        // BR-080: every one of the 15 roster players, regardless of SelectionRole — unlike
        // FantasyPoints, which BR-044 restricts to the Starting XI above.
        var totalGoals = goalsByPlayerId.Values.Sum();
        var totalOwnGoals = ownGoalsByPlayerId.Values.Sum();
        var goalkeeperGoalsConceded = playerIds
            .Where(id => positionByPlayerId.GetValueOrDefault(id) == PlayerPosition.Gk)
            .Select(id => goalsConcededByPlayerId[id])
            .ToList();
        var defenderGoalsConceded = playerIds
            .Where(id => positionByPlayerId.GetValueOrDefault(id) == PlayerPosition.Def)
            .Select(id => goalsConcededByPlayerId[id])
            .ToList();

        var (goalsFor, goalsAgainst, goalDifference) = GameweekScore.CalculateFantasyGoals(
            totalGoals, goalkeeperGoalsConceded, defenderGoalsConceded, totalOwnGoals);

        return (fantasyPoints, captainPoints, goalsFor, goalsAgainst, goalDifference);
    }

    /// <summary>IT-56: batches the (LeagueMembershipId, UserId) lookup every newly-scored FantasyTeam needs for its WeeklyScore NotificationRequest, rather than one query per team.</summary>
    private async Task<Dictionary<Guid, (Guid LeagueMembershipId, Guid UserId)>> ResolveMembershipsAsync(
        List<Guid> fantasyTeamIds, CancellationToken cancellationToken) =>
        await (
            from fantasyTeam in dbContext.FantasyTeams
            join membership in dbContext.LeagueMemberships on fantasyTeam.LeagueMembershipId equals membership.LeagueMembershipId
            where fantasyTeamIds.Contains(fantasyTeam.FantasyTeamId)
            select new { fantasyTeam.FantasyTeamId, fantasyTeam.LeagueMembershipId, membership.UserId }
        ).ToDictionaryAsync(x => x.FantasyTeamId, x => (x.LeagueMembershipId, x.UserId), cancellationToken);

    /// <summary>A player with no PlayerPerformance row at all this Gameweek contributes zero for every statistic — nothing to resolve a value from. Otherwise routes through IAuthoritativeValueResolver so an active ScoreOverride still takes precedence (Invariant 12).</summary>
    private async Task<int> ResolveStatAsync(
        PlayerPerformance? performance,
        string overrideJsonKey,
        Func<PlayerPerformance, int> officialSelector,
        CancellationToken cancellationToken)
    {
        if (performance is null)
        {
            return 0;
        }

        return await authoritativeValueResolver.ResolveAsync(
            performance.PlayerPerformanceId,
            fromActiveOverride: o => ExtractOverriddenStat(o, overrideJsonKey, officialSelector(performance)),
            fromOfficialData: officialSelector,
            applicationCalculation: () => 0,
            cancellationToken);
    }

    /// <summary>
    /// An active ScoreOverride's payload is keyed by whichever PlayerPerformance field(s) it
    /// corrects (e.g. <c>{"goals":2}</c> — db-tests/060_scoring_invariants.sql's own example), not
    /// necessarily <paramref name="jsonKey"/> itself — an override of a *different* statistic
    /// leaves this one untouched, so <paramref name="officialValue"/> (already resolved by the
    /// caller, before this synchronous delegate runs) is the fallback.
    /// </summary>
    private static int ExtractOverriddenStat(ScoreOverride activeOverride, string jsonKey, int officialValue)
    {
        using var overrideValue = JsonDocument.Parse(activeOverride.OverrideValueJson);
        return overrideValue.RootElement.TryGetProperty(jsonKey, out var value)
            ? value.GetInt32()
            : officialValue;
    }
}
