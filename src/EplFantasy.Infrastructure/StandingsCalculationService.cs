using System.Text.Json;
using EplFantasy.Competition;
using EplFantasy.FantasyTeams;
using EplFantasy.Notifications;
using EplFantasy.PlayerData;
using EplFantasy.Scoring;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>
/// IT-42 (F-010.1, BR-118/BR-215-BR-217): tallies each FantasyTeam's own Played/Won/Drawn/Lost/
/// League Points/Fantasy Goals For/Against/Captain Points from every already-decided
/// <see cref="HeadToHeadMatch"/> up to and including the given Gameweek's own Number (BR-217's
/// point-in-time semantics — a later Gameweek's matches must never leak into an earlier snapshot),
/// then ranks the full set via the Season's own configured <see cref="StandingsTieBreakPipeline"/>
/// (BR-216 — never a partial/ad hoc ordering) before persisting. A from-scratch recomputation every
/// time, the same "re-derive, don't incrementally patch" convention <see cref="GameweekScore"/>/
/// <see cref="HeadToHeadMatch"/> already established — any existing rows for this exact
/// (Season, AsOfGameweek) snapshot are replaced, never merged into.
///
/// IT-56 (F-012.3, BR-154/BR-338): every call queues a `WeeklyStandings` NotificationRequest per
/// FantasyTeam alongside the replaced snapshot, carrying that FantasyTeam's newly-assigned
/// <see cref="LeagueStanding.Position"/> (AC2). Unlike <c>GameweekScoreCalculationService</c>, this
/// method has no first-time-only guard of its own — a snapshot is always fully replaced, whatever
/// call number this is — so a notification fires on every call, matching BR-154's literal "when
/// [LeagueStanding] is recalculated" wording rather than inventing an idempotency guard this
/// method's own domain doesn't otherwise track. As in IT-55/this task's own WeeklyScore half,
/// preferences are never read here — one row per <see cref="NotificationChannel"/> is queued
/// unconditionally, left entirely to the outbox dispatcher (IT-F12) to suppress per BR-226.
/// </summary>
public sealed class StandingsCalculationService(
    EplFantasyDbContext dbContext,
    IStandingsTieBreakContextLoader tieBreakContextLoader,
    StandingsTieBreakPipeline tieBreakPipeline,
    IClock clock) : IStandingsCalculationService
{
    private sealed record WeeklyStandingsPayload(Guid AsOfGameweekId, int Position);

    public async Task CalculateAsync(Guid seasonId, Guid asOfGameweekId, CancellationToken cancellationToken = default)
    {
        var season = await dbContext.Seasons.SingleAsync(s => s.SeasonId == seasonId, cancellationToken);
        var asOfGameweek = await dbContext.Gameweeks.SingleAsync(g => g.GameweekId == asOfGameweekId, cancellationToken);

        var gameweekIdsUpToAsOf = await dbContext.Gameweeks
            .Where(g => g.EplSeasonIdentifier == season.EplSeasonIdentifier && g.Number <= asOfGameweek.Number)
            .Select(g => g.GameweekId)
            .ToListAsync(cancellationToken);

        var fantasyTeamIds = await dbContext.FantasyTeams
            .Where(t => t.SeasonId == seasonId)
            .Select(t => t.FantasyTeamId)
            .ToListAsync(cancellationToken);

        // BR-111-BR-114: only a match both sides have actually been scored for has a Result —
        // an unplayed fixture (including one for a later Gameweek than gameweekIdsUpToAsOf covers)
        // contributes nothing to Played/Won/Drawn/Lost/League Points/Fantasy Goals.
        var decidedMatches = await dbContext.HeadToHeadMatches
            .Where(m => m.SeasonId == seasonId && gameweekIdsUpToAsOf.Contains(m.GameweekId) && m.Result != null)
            .ToListAsync(cancellationToken);

        // BR-051: cumulative across every scored Gameweek up to this snapshot, including bye
        // weeks (BR-306) that have no HeadToHeadMatch row at all — Captain Points accrue
        // independent of whether that Gameweek had a scheduled opponent.
        var captainPointsByFantasyTeamId = await dbContext.GameweekScores
            .Where(s => fantasyTeamIds.Contains(s.FantasyTeamId) && gameweekIdsUpToAsOf.Contains(s.GameweekId))
            .GroupBy(s => s.FantasyTeamId)
            .Select(g => new { FantasyTeamId = g.Key, CaptainPoints = g.Sum(s => s.CaptainPoints) })
            .ToDictionaryAsync(x => x.FantasyTeamId, x => x.CaptainPoints, cancellationToken);

        var standings = fantasyTeamIds
            .Select(fantasyTeamId => Tally(seasonId, fantasyTeamId, asOfGameweekId, decidedMatches, captainPointsByFantasyTeamId.GetValueOrDefault(fantasyTeamId)))
            .ToList();

        var tieBreakContext = await tieBreakContextLoader.LoadAsync(seasonId, cancellationToken);
        var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == seasonId, cancellationToken);
        var comparer = tieBreakPipeline.GetComparer(seasonConfiguration.TieBreakRulesetVersion, tieBreakContext);

        standings.Sort(comparer);
        for (var i = 0; i < standings.Count; i++)
        {
            standings[i].AssignPosition(i + 1);
        }

        var existingRows = await dbContext.LeagueStandings
            .Where(s => s.SeasonId == seasonId && s.AsOfGameweekId == asOfGameweekId)
            .ToListAsync(cancellationToken);
        dbContext.LeagueStandings.RemoveRange(existingRows);
        dbContext.LeagueStandings.AddRange(standings);

        var now = clock.UtcNow;
        var membershipByFantasyTeamId = await ResolveMembershipsAsync(fantasyTeamIds, cancellationToken);
        foreach (var standing in standings)
        {
            var (membershipId, userId) = membershipByFantasyTeamId[standing.FantasyTeamId];
            var payloadJson = JsonSerializer.Serialize(new WeeklyStandingsPayload(asOfGameweekId, standing.Position));
            foreach (var channel in Enum.GetValues<NotificationChannel>())
            {
                dbContext.NotificationRequests.Add(new NotificationRequest
                {
                    RequestId = Guid.NewGuid(),
                    UserId = userId,
                    LeagueMembershipId = membershipId,
                    EventType = NotificationEventType.WeeklyStandings,
                    Channel = channel,
                    PayloadJson = payloadJson,
                    Status = NotificationStatus.Pending,
                    CreatedAt = now,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>IT-56: batches the (LeagueMembershipId, UserId) lookup every FantasyTeam in this Season needs for its WeeklyStandings NotificationRequest, rather than one query per team.</summary>
    private async Task<Dictionary<Guid, (Guid LeagueMembershipId, Guid UserId)>> ResolveMembershipsAsync(
        List<Guid> fantasyTeamIds, CancellationToken cancellationToken) =>
        await (
            from fantasyTeam in dbContext.FantasyTeams
            join membership in dbContext.LeagueMemberships on fantasyTeam.LeagueMembershipId equals membership.LeagueMembershipId
            where fantasyTeamIds.Contains(fantasyTeam.FantasyTeamId)
            select new { fantasyTeam.FantasyTeamId, fantasyTeam.LeagueMembershipId, membership.UserId }
        ).ToDictionaryAsync(x => x.FantasyTeamId, x => (x.LeagueMembershipId, x.UserId), cancellationToken);

    private static LeagueStanding Tally(
        Guid seasonId, Guid fantasyTeamId, Guid asOfGameweekId, List<HeadToHeadMatch> decidedMatches, int captainPointsTotal)
    {
        var played = 0;
        var won = 0;
        var drawn = 0;
        var lost = 0;
        var leaguePoints = 0;
        var goalsFor = 0;
        var goalsAgainst = 0;

        foreach (var match in decidedMatches)
        {
            bool isHome;
            if (match.HomeFantasyTeamId == fantasyTeamId)
            {
                isHome = true;
            }
            else if (match.AwayFantasyTeamId == fantasyTeamId)
            {
                isHome = false;
            }
            else
            {
                continue;
            }

            played++;
            goalsFor += (isHome ? match.HomeScore : match.AwayScore) ?? 0;
            goalsAgainst += (isHome ? match.AwayScore : match.HomeScore) ?? 0;
            leaguePoints += (isHome ? match.LeaguePointsHome : match.LeaguePointsAway) ?? 0;

            switch (match.Result)
            {
                case MatchResult.Draw:
                    drawn++;
                    break;
                case MatchResult.HomeWin when isHome:
                case MatchResult.AwayWin when !isHome:
                    won++;
                    break;
                default:
                    lost++;
                    break;
            }
        }

        return LeagueStanding.Calculate(
            seasonId, fantasyTeamId, asOfGameweekId,
            leaguePoints, played, won, drawn, lost,
            goalsFor, goalsAgainst, goalsFor - goalsAgainst,
            captainPointsTotal);
    }
}
