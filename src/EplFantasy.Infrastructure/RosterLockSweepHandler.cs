using EplFantasy.Leagues;
using EplFantasy.PlayerData;
using EplFantasy.Rosters;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EplFantasy.Infrastructure;

/// <summary>
/// IT-31 (F-007.3, BR-093–BR-096, BR-305): ADR-012/IT-F08's sweep, applied to the GameweekRoster
/// aggregate. Every tick, for every Gameweek whose RosterLockDeadline (BR-093, set at IT-18 sync
/// time) has passed: locks every already-Submitted roster (BR-094), and — separately — carries
/// forward a Locked roster (BR-305) for every FantasyTeam that never submitted one at all. Both
/// halves are naturally idempotent across ticks: a Locked roster is no longer selected by the
/// first query, and a FantasyTeam that already has *any* GameweekRoster row for this Gameweek
/// (submitted or already carried-forward) is excluded from the second.
/// </summary>
public sealed class RosterLockSweepHandler(
    EplFantasyDbContext dbContext,
    IClock clock,
    ILogger<RosterLockSweepHandler> logger) : IDeadlineSweepHandler
{
    public string Name => "GameweekRosterLock";

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var overdueGameweeks = await dbContext.Gameweeks
            .Where(g => g.RosterLockDeadline <= now)
            .ToListAsync(cancellationToken);

        if (overdueGameweeks.Count == 0)
        {
            return;
        }

        foreach (var gameweek in overdueGameweeks)
        {
            await LockSubmittedRostersAsync(gameweek, now, cancellationToken);
            await CarryForwardMissingRostersAsync(gameweek, now, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>AC3/BR-094: every roster a FantasyTeam actually submitted before the deadline just transitions Submitted → Locked, untouched otherwise.</summary>
    private async Task LockSubmittedRostersAsync(Gameweek gameweek, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var submittedRosters = await dbContext.GameweekRosters
            .Where(r => r.GameweekId == gameweek.GameweekId && r.Status == RosterStatus.Submitted)
            .ToListAsync(cancellationToken);

        foreach (var roster in submittedRosters)
        {
            roster.Lock(now);
            logger.LogInformation(
                "{Event}: GameweekRoster {GameweekRosterId} locked for Gameweek {GameweekId}",
                "GameweekRosterLocked", roster.GameweekRosterId, gameweek.GameweekId);
        }
    }

    /// <summary>AC6-AC8/BR-305: every FantasyTeam that owes this Gameweek a roster but never submitted one at all gets its last Locked (or Scored — a Gameweek that has since scored was Locked at some point too) roster copied forward, minus any player it no longer owns, then locked immediately.</summary>
    private async Task CarryForwardMissingRostersAsync(Gameweek gameweek, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fantasyTeamIds = await dbContext.FantasyTeams
            .Where(ft => dbContext.Seasons.Any(s => s.SeasonId == ft.SeasonId && s.EplSeasonIdentifier == gameweek.EplSeasonIdentifier))
            .Select(ft => ft.FantasyTeamId)
            .ToListAsync(cancellationToken);

        if (fantasyTeamIds.Count == 0)
        {
            return;
        }

        var fantasyTeamIdsWithARoster = await dbContext.GameweekRosters
            .Where(r => r.GameweekId == gameweek.GameweekId)
            .Select(r => r.FantasyTeamId)
            .ToListAsync(cancellationToken);

        var missingFantasyTeamIds = fantasyTeamIds.Except(fantasyTeamIdsWithARoster).ToList();

        foreach (var fantasyTeamId in missingFantasyTeamIds)
        {
            var priorRoster = await dbContext.GameweekRosters.Include(r => r.Players)
                .Where(r => r.FantasyTeamId == fantasyTeamId && (r.Status == RosterStatus.Locked || r.Status == RosterStatus.Scored))
                .OrderByDescending(r => r.LockedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var carriedForwardPlayerIds = new List<Guid>();
            Guid? captainPlayerId = null;

            if (priorRoster is not null)
            {
                var priorPlayerIds = priorRoster.Players.Select(p => p.PlayerId).ToList();
                var stillOwnedPlayerIds = await dbContext.SquadPlayers
                    .Where(sp => sp.FantasyTeamId == fantasyTeamId && sp.IsCurrentlyOwned && priorPlayerIds.Contains(sp.PlayerId))
                    .Select(sp => sp.PlayerId)
                    .ToListAsync(cancellationToken);

                carriedForwardPlayerIds = priorRoster.Players
                    .Select(p => p.PlayerId)
                    .Where(stillOwnedPlayerIds.Contains)
                    .ToList();

                if (priorRoster.CaptainPlayerId is { } priorCaptainId && carriedForwardPlayerIds.Contains(priorCaptainId))
                {
                    captainPlayerId = priorCaptainId;
                }
            }

            var carriedForward = GameweekRoster.CreateCarriedForward(
                Guid.NewGuid(), fantasyTeamId, gameweek.GameweekId, carriedForwardPlayerIds, captainPlayerId, now);
            carriedForward.Lock(now);
            dbContext.GameweekRosters.Add(carriedForward);

            logger.LogInformation(
                "{Event}: FantasyTeam {FantasyTeamId} carried forward {PlayerCount} player(s) and locked for Gameweek {GameweekId}",
                "GameweekRosterCarriedForward", fantasyTeamId, carriedForwardPlayerIds.Count, gameweek.GameweekId);
        }
    }
}
