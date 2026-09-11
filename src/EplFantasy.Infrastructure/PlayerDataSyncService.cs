using EplFantasy.Administration;
using EplFantasy.Drafts;
using EplFantasy.FantasyTeams;
using EplFantasy.PlayerData;
using EplFantasy.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace EplFantasy.Infrastructure;

/// <summary>
/// The idempotent-upsert half of ADR-009's PlayerDataIntegration module — matches every row on
/// its external identifier (never blind-inserts, AP-008/BR-232) and commits each sync call as one
/// atomic batch via a single SaveChangesAsync (BR-233: a failure partway through leaves prior data
/// untouched, never a partial commit). <see cref="IFplDataSource"/> is the only thing this class
/// depends on for the actual external data — it never sees a raw external shape itself, only the
/// already-translated sync records.
/// </summary>
public sealed class PlayerDataSyncService(
    EplFantasyDbContext dbContext,
    IFplDataSource dataSource,
    IClock clock,
    IAdministrativeActionRecorder administrativeActionRecorder) : IPlayerDataSyncService
{
    public async Task SyncClubsAsync(CancellationToken cancellationToken = default)
    {
        var externalClubs = await dataSource.GetClubsAsync(cancellationToken);
        var existingByExternalId = await dbContext.Clubs.ToDictionaryAsync(c => c.EplClubId, cancellationToken);

        foreach (var external in externalClubs)
        {
            if (existingByExternalId.TryGetValue(external.EplClubId, out var existing))
            {
                existing.Name = external.Name;
                existing.ShortName = external.ShortName;
            }
            else
            {
                dbContext.Clubs.Add(new Club
                {
                    ClubId = Guid.NewGuid(),
                    EplClubId = external.EplClubId,
                    Name = external.Name,
                    ShortName = external.ShortName,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SyncPlayersAsync(CancellationToken cancellationToken = default)
    {
        var externalPlayers = await dataSource.GetPlayersAsync(cancellationToken);
        var clubIdsByExternalId = await dbContext.Clubs.ToDictionaryAsync(c => c.EplClubId, c => c.ClubId, cancellationToken);
        var existingByExternalId = await dbContext.Players.ToDictionaryAsync(p => p.EplPlayerId, cancellationToken);

        // IT-21 (F-004.4, BR-066/BR-071/BR-308): a Player transitioning from "had a club" to "has
        // none" this call — never a Player who was already clubless before this sync ran, which is
        // not a transition at all — is the one automatic EPL-exit path (distinct from IT-49's
        // Administrator-driven season-ending-injury path). Collected during the loop below, handled
        // after it so every Player row is upserted first.
        var exitedPlayerIds = new List<Guid>();

        foreach (var external in externalPlayers)
        {
            Guid? currentClubId = null;
            if (external.CurrentEplClubId is not null)
            {
                if (!clubIdsByExternalId.TryGetValue(external.CurrentEplClubId, out var resolvedClubId))
                {
                    // A malformed/out-of-order batch — fail the whole sync (BR-233) rather than
                    // silently persisting a player with a dropped club reference.
                    throw new InvalidOperationException(
                        $"Player \"{external.EplPlayerId}\" references unknown club \"{external.CurrentEplClubId}\" — sync clubs before players.");
                }

                currentClubId = resolvedClubId;
            }

            if (existingByExternalId.TryGetValue(external.EplPlayerId, out var existing))
            {
                var wasInEpl = existing.CurrentClubId is not null;
                existing.Name = external.Name;
                existing.Position = external.Position;
                existing.CurrentClubId = currentClubId;

                if (wasInEpl && currentClubId is null)
                {
                    exitedPlayerIds.Add(existing.PlayerId);
                }
            }
            else
            {
                dbContext.Players.Add(new Player
                {
                    PlayerId = Guid.NewGuid(),
                    EplPlayerId = external.EplPlayerId,
                    Name = external.Name,
                    Position = external.Position,
                    CurrentClubId = currentClubId,
                });
            }
        }

        foreach (var playerId in exitedPlayerIds)
        {
            await GrantReplacementEligibilityForExitAsync(playerId, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// IT-21 (BR-066/BR-308): every currently-owned SquadPlayer row for the exiting Player — across
    /// every FantasyTeam that owns them, in any League/Season — becomes replacement-eligible
    /// (V005's ReplacementEligibleAt), and each owning FantasyTeam is granted exactly one spendable
    /// ReplacementOpportunity (BR-307: never expires), unless BR-287's
    /// SeasonConfiguration.ReplacementSelectionCap for that FantasyTeam's Season has already been
    /// reached (a null cap means uncapped). Each grant is also an AdministrativeAction with
    /// <c>actingMembershipId: null</c> — IAdministrativeActionRecorder's own documented convention
    /// for a system-generated entry, not an Administrator's — so this automatic path stays
    /// distinguishable in the audit trail from IT-49's Administrator-driven injury path.
    /// </summary>
    private async Task GrantReplacementEligibilityForExitAsync(Guid playerId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var ownedSquadPlayers = await dbContext.SquadPlayers
            .Where(sp => sp.PlayerId == playerId && sp.IsCurrentlyOwned)
            .ToListAsync(cancellationToken);

        foreach (var squadPlayer in ownedSquadPlayers)
        {
            squadPlayer.ReplacementEligibleAt = now;

            var fantasyTeam = await dbContext.FantasyTeams.SingleAsync(ft => ft.FantasyTeamId == squadPlayer.FantasyTeamId, cancellationToken);
            var leagueMembership = await dbContext.LeagueMemberships.SingleAsync(m => m.LeagueMembershipId == fantasyTeam.LeagueMembershipId, cancellationToken);
            var seasonConfiguration = await dbContext.SeasonConfigurations.SingleAsync(sc => sc.SeasonId == fantasyTeam.SeasonId, cancellationToken);

            var grantedSoFar = await dbContext.ReplacementOpportunities.CountAsync(ro => ro.FantasyTeamId == fantasyTeam.FantasyTeamId, cancellationToken);
            if (!ReplacementOpportunityPolicy.CanGrantAnotherOpportunity(seasonConfiguration.ReplacementSelectionCap, grantedSoFar))
            {
                continue; // BR-287: this FantasyTeam already has its cap's worth — eligibility is marked above regardless, but no further token.
            }

            var opportunity = new ReplacementOpportunity
            {
                ReplacementOpportunityId = Guid.NewGuid(),
                FantasyTeamId = fantasyTeam.FantasyTeamId,
                SourcePlayerId = playerId,
                GrantedAt = now,
                GrantReason = ReplacementGrantReason.EplExit,
                SpentAt = null,
            };
            dbContext.ReplacementOpportunities.Add(opportunity);

            administrativeActionRecorder.Record(
                leagueMembership.LeagueId,
                actingMembershipId: null,
                AdminActionType.ReplacementEligibilityGranted,
                targetEntityType: "ReplacementOpportunity",
                targetEntityId: opportunity.ReplacementOpportunityId,
                beforeState: new { },
                afterState: new { opportunity.FantasyTeamId, opportunity.SourcePlayerId, GrantReason = opportunity.GrantReason.ToString() });
        }
    }

    public async Task SyncGameweeksAndFixturesAsync(string eplSeasonIdentifier, CancellationToken cancellationToken = default)
    {
        if (!await dbContext.EplSeasons.AnyAsync(s => s.EplSeasonIdentifier == eplSeasonIdentifier, cancellationToken))
        {
            dbContext.EplSeasons.Add(new EplSeason { EplSeasonIdentifier = eplSeasonIdentifier });
        }

        var existingGameweeksByNumber = await dbContext.Gameweeks
            .Where(g => g.EplSeasonIdentifier == eplSeasonIdentifier)
            .ToDictionaryAsync(g => g.Number, cancellationToken);

        foreach (var external in await dataSource.GetGameweeksAsync(eplSeasonIdentifier, cancellationToken))
        {
            if (existingGameweeksByNumber.TryGetValue(external.Number, out var existing))
            {
                existing.RosterLockDeadline = external.RosterLockDeadline;
            }
            else
            {
                var gameweek = new Gameweek
                {
                    GameweekId = Guid.NewGuid(),
                    EplSeasonIdentifier = eplSeasonIdentifier,
                    Number = external.Number,
                    RosterLockDeadline = external.RosterLockDeadline,
                };
                dbContext.Gameweeks.Add(gameweek);
                existingGameweeksByNumber[external.Number] = gameweek; // visible to fixtures below in this same batch.
            }
        }

        var clubIdsByExternalId = await dbContext.Clubs.ToDictionaryAsync(c => c.EplClubId, c => c.ClubId, cancellationToken);
        var existingFixturesByExternalId = await dbContext.Fixtures.ToDictionaryAsync(f => f.EplFixtureId, cancellationToken);

        foreach (var external in await dataSource.GetFixturesAsync(eplSeasonIdentifier, cancellationToken))
        {
            if (!existingGameweeksByNumber.TryGetValue(external.GameweekNumber, out var gameweek))
            {
                throw new InvalidOperationException(
                    $"Fixture \"{external.EplFixtureId}\" references Gameweek {external.GameweekNumber}, which was not included in this sync batch.");
            }

            if (!clubIdsByExternalId.TryGetValue(external.HomeEplClubId, out var homeClubId))
            {
                throw new InvalidOperationException($"Fixture \"{external.EplFixtureId}\" references unknown home club \"{external.HomeEplClubId}\".");
            }

            if (!clubIdsByExternalId.TryGetValue(external.AwayEplClubId, out var awayClubId))
            {
                throw new InvalidOperationException($"Fixture \"{external.EplFixtureId}\" references unknown away club \"{external.AwayEplClubId}\".");
            }

            if (existingFixturesByExternalId.TryGetValue(external.EplFixtureId, out var existing))
            {
                existing.GameweekId = gameweek.GameweekId;
                existing.HomeClubId = homeClubId;
                existing.AwayClubId = awayClubId;
                // IT-22 (BR-100): a null external.KickoffTime means FPL currently reports no
                // confirmed time (Postponed) — fixtures.kickoff_time is NOT NULL, so the
                // last-confirmed time is preserved rather than cleared; it's overwritten again the
                // moment FPL confirms a new one (rescheduled or otherwise).
                if (external.KickoffTime is not null)
                {
                    existing.KickoffTime = external.KickoffTime.Value;
                }

                existing.Status = external.Status;
                existing.HomeGoals = external.HomeGoals;
                existing.AwayGoals = external.AwayGoals;
            }
            else
            {
                if (external.KickoffTime is null)
                {
                    // A fixture never seen before with no confirmed time at all has nothing to
                    // anchor the NOT NULL kickoff_time column to yet — wait for a later sync once
                    // FPL confirms one, rather than guessing (BR-289's "fail safe, not fail open").
                    continue;
                }

                var fixture = new Fixture
                {
                    FixtureId = Guid.NewGuid(),
                    EplFixtureId = external.EplFixtureId,
                    GameweekId = gameweek.GameweekId,
                    HomeClubId = homeClubId,
                    AwayClubId = awayClubId,
                    KickoffTime = external.KickoffTime.Value,
                    Status = external.Status,
                    HomeGoals = external.HomeGoals,
                    AwayGoals = external.AwayGoals,
                };
                dbContext.Fixtures.Add(fixture);
                existingFixturesByExternalId[external.EplFixtureId] = fixture; // IT-19: visible to the standings recompute below, in this same batch.
            }
        }

        // IT-19 (F-004.6, BR-329/BR-330/BR-334): recompute this season's real-world EPL table from
        // every one of its fixtures, not just this batch — using the in-memory, already-up-to-date
        // state built above rather than a fresh query (a query here would either miss this batch's
        // own new fixtures or see stale values for its updates, since nothing has been flushed to
        // the database yet). This keeps the whole sync one atomic SaveChangesAsync (BR-233) instead
        // of a second, separately-committed round trip.
        var seasonGameweekIds = existingGameweeksByNumber.Values.Select(g => g.GameweekId).ToHashSet();
        var seasonFixtures = existingFixturesByExternalId.Values.Where(f => seasonGameweekIds.Contains(f.GameweekId));
        await RecomputeClubStandingsAsync(eplSeasonIdentifier, seasonFixtures, cancellationToken);

        // EplSeason + every Gameweek + every Fixture + the recomputed ClubStanding table all commit
        // together, in this dependency order, as one batch (BR-233) — a failure anywhere above
        // (e.g. an unknown club reference) means NONE of it persists, not a partial sync.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// IT-19 (F-004.6): the real-world EPL table (BR-329/BR-330), recomputed from Fixture results
    /// with no Administrator-override path at all (BR-334, distinct from Fantasy LeagueStanding's
    /// own IStandingsTieBreakRuleset) — the only inputs are this season's fixtures, matched on
    /// (EplSeasonIdentifier, ClubId) the same upsert-by-external-identifier way every other row
    /// here is (AP-008). Ordered by BR-330's "official EPL rules": Points, then Goal Difference,
    /// then Goals For — the practical ordering for a reference/display table; genuine head-to-head
    /// tie-breaks are not implemented. A Club appears once it has at least one fixture for this
    /// season, even at 0 played, so the table always lists every competing Club, not just ones that
    /// have played.
    /// </summary>
    private async Task RecomputeClubStandingsAsync(string eplSeasonIdentifier, IEnumerable<Fixture> seasonFixtures, CancellationToken cancellationToken)
    {
        var tallies = new Dictionary<Guid, ClubTally>();

        foreach (var fixture in seasonFixtures)
        {
            tallies.TryAdd(fixture.HomeClubId, new ClubTally());
            tallies.TryAdd(fixture.AwayClubId, new ClubTally());

            if (fixture.Status != FixtureStatus.Completed || fixture.HomeGoals is null || fixture.AwayGoals is null)
            {
                continue;
            }

            var home = tallies[fixture.HomeClubId];
            var away = tallies[fixture.AwayClubId];
            home.Played++;
            away.Played++;
            home.GoalsFor += fixture.HomeGoals.Value;
            home.GoalsAgainst += fixture.AwayGoals.Value;
            away.GoalsFor += fixture.AwayGoals.Value;
            away.GoalsAgainst += fixture.HomeGoals.Value;

            if (fixture.HomeGoals > fixture.AwayGoals)
            {
                home.Won++;
                home.Points += 3;
                away.Lost++;
            }
            else if (fixture.HomeGoals < fixture.AwayGoals)
            {
                away.Won++;
                away.Points += 3;
                home.Lost++;
            }
            else
            {
                home.Drawn++;
                home.Points += 1;
                away.Drawn++;
                away.Points += 1;
            }
        }

        var ranked = tallies
            .OrderByDescending(kvp => kvp.Value.Points)
            .ThenByDescending(kvp => kvp.Value.GoalsFor - kvp.Value.GoalsAgainst)
            .ThenByDescending(kvp => kvp.Value.GoalsFor)
            .ToList();

        var existingStandingsByClubId = await dbContext.ClubStandings
            .Where(s => s.EplSeasonIdentifier == eplSeasonIdentifier)
            .ToDictionaryAsync(s => s.ClubId, cancellationToken);

        for (var i = 0; i < ranked.Count; i++)
        {
            var clubId = ranked[i].Key;
            var tally = ranked[i].Value;
            var position = i + 1;

            if (existingStandingsByClubId.TryGetValue(clubId, out var standing))
            {
                standing.Position = position;
                standing.Played = tally.Played;
                standing.Won = tally.Won;
                standing.Drawn = tally.Drawn;
                standing.Lost = tally.Lost;
                standing.GoalsFor = tally.GoalsFor;
                standing.GoalsAgainst = tally.GoalsAgainst;
                standing.Points = tally.Points;
            }
            else
            {
                dbContext.ClubStandings.Add(new ClubStanding
                {
                    EplSeasonIdentifier = eplSeasonIdentifier,
                    ClubId = clubId,
                    Position = position,
                    Played = tally.Played,
                    Won = tally.Won,
                    Drawn = tally.Drawn,
                    Lost = tally.Lost,
                    GoalsFor = tally.GoalsFor,
                    GoalsAgainst = tally.GoalsAgainst,
                    Points = tally.Points,
                });
            }
        }
    }

    private sealed class ClubTally
    {
        public int Played;
        public int Won;
        public int Drawn;
        public int Lost;
        public int GoalsFor;
        public int GoalsAgainst;
        public int Points;
    }
}
